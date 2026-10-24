using System;
using System.Linq;
using Nexus.Service.Lighting;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Singleton coordinator for the SLV3 wireless link: owns the TX + RX dongle
/// transports, the master identity, the bind/unbind state machine, the
/// device-list poll, RGB pushes, and the PWM sync. The cadence and every
/// frame mirror L-Connect's MasterDevice loop as captured on the Y70 with
/// USBPcap (2026-09-04): <see cref="PollTick"/> is its RefreshList +
/// SyncControlInfo pass, <see cref="DriveTick"/> adds the once-a-second
/// SyncPwm, QuerryMasterMac, SyncMasterClock and SaveConfig work.
/// All hardware I/O and shared state are guarded by one lock: the connection
/// worker's ticks and route-driven bind/unbind/identify calls both touch it.
/// </summary>
public sealed partial class Slv3Hub : IDisposable
{
    // Shared read-only default (all null = motherboard-sync) for a chain that
    // has never had a duty set. Never mutated - safe to share across keys.
    private static readonly int?[] DefaultDutyTargets = new int?[Slv3Protocol.PortsPerRecord];

    private readonly ISlv3Discovery _discovery;
    private readonly Func<Slv3PortInfo, ISlv3Transport> _transportFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slv3PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeferredUnbind> _deferredUnbinds = new(StringComparer.Ordinal);
    // Last cmd_seq issued per chain MAC hex, so back-to-back commands stay
    // monotonic even before the chain's record echoes the previous one
    // (L-Connect's per-device targe_cmd_seq).
    private readonly Dictionary<string, byte> _lastIssuedSeq = new(StringComparer.Ordinal);
    private List<Slv3DeviceRecord> _lastFanRecords = new();

    // Last-known chains keyed by MAC hex. Under RGB traffic the RX misses
    // beacons, so a poll's record set is a SAMPLE of the live chains, not the
    // truth; records merge in and only expire after ChainExpiryMs unseen.
    // Wholesale replacement per poll is what made chains flap in and out of
    // cooling/lighting (Y70 log: "device list: 2 -> 1 -> 2" continuously).
    private readonly Dictionary<string, Slv3KnownChain> _knownChains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slv3BindingLog> _bindingLogs = new(StringComparer.Ordinal);
    internal const long BindingLogIntervalMs = 60_000;

    // A validated record can still carry a corrupted MAC, so a new chain is
    // listed only once enough polls within the confirm window repeat it.
    private readonly Dictionary<string, Slv3Unconfirmed> _unconfirmedChains = new(StringComparer.Ordinal);
    private long _devicePolls;
    internal const int ChainConfirmWindowPolls = 10;
    internal const int ChainConfirmSightings = 3;

    /// <summary>False lists a new chain on first sight.</summary>
    internal bool ConfirmNewChains { get; init; } = true;

    // Per-chain PWM port targets keyed by fan MAC hex; a missing key or a
    // null element means that port follows the motherboard PWM header. Read
    // by the PWM sync, written by SetPortDuty; both hold _lock.
    private readonly Dictionary<string, int?[]> _dutyTargets = new(StringComparer.Ordinal);

    // HydroShift IIs under RF control (pump driven or screen saved), keyed by AIO
    // MAC hex. An AIO without an entry is sent no params.
    private readonly Dictionary<string, Slv3AioControl> _aioControl = new(StringComparer.Ordinal);
    private volatile bool _anyAioControlled;
    private volatile bool _anyAioBound;

    /// <summary>Host readings for a HydroShift II's LCD; read once per <see cref="DriveTick"/>, outside the hub lock.</summary>
    public Func<Slv3AioSensors>? AioSensors { get; set; }

    /// <summary>Radio MAC hex of the HydroShift II on the USB link, or null; read once per tick, outside the hub lock.</summary>
    public Func<string?>? UsbAioMac { get; set; }
    private volatile string? _usbAioMac;

    /// <summary>Tells whether a MAC belongs to a fan that is plugged in over USB and driven by its own handler; the radio never binds, steers or writes to it.</summary>
    public Func<string, bool>? WiredOwned { get; set; }

    internal const string WiredOwnedError = "connected by USB";

    /// <summary>A wired fan that is being released from the radio: the only thing reconcile does for it.</summary>
    private bool IsReleasingLocked(string key)
    {
        if (!_intents.TryGetValue(key, out var intent) || !intent.Unbinding)
        {
            return false;
        }
        if (intent.Sends >= WiredReleaseMaxSends)
        {
            // A wired fan that never lets go is left alone; a later Unbind starts a fresh attempt.
            _intents.Remove(key);
            return false;
        }
        return true;
    }

    /// <summary>Unbind frames, the first one included, a release of a wired fan sends before it is given up.</summary>
    internal const int WiredReleaseMaxSends = 20;

    private bool IsWiredOwned(string macHex) => !string.IsNullOrEmpty(macHex) && WiredOwned?.Invoke(macHex) == true;

    /// <summary>A USB-connected fan the radio must leave alone: one bound to this PC's radio is driven over the radio, the single owner.</summary>
    private bool IsWiredOnly(string macHex)
    {
        if (!IsWiredOwned(macHex))
        {
            return false;
        }
        if (!TryParseMac(macHex, out var mac))
        {
            return true;
        }
        lock (_lock)
        {
            return !(TryFindRecordLocked(mac, out var record) && IsBoundToUsLocked(record));
        }
    }

    /// <summary>Saved HydroShift II screens by AIO MAC hex; read once per <see cref="DriveTick"/>, outside the hub lock.</summary>
    public Func<IReadOnlyDictionary<string, Slv3AioScreen>>? AioScreens { get; set; }

    /// <summary>
    /// Raised under the hub lock with an AIO's MAC hex when it acknowledges RF control.
    /// The firmware then draws its own screen over whatever its USB link last showed.
    /// </summary>
    public event Action<string>? AioSwitched;

    /// <summary>Persisted MAC hex to last slot of the devices Nexus owns; read once, outside the hub lock.</summary>
    public Func<IReadOnlyDictionary<string, int>>? OwnedDevicesLoad { get; set; }

    /// <summary>Persists the owned-device list; invoked outside the hub lock after the list changes.</summary>
    public Action<IReadOnlyDictionary<string, int>>? OwnedDevicesSave { get; set; }

    // Devices Nexus bound to its master, by MAC hex, with the slot each last held.
    private readonly Dictionary<string, int> _owned = new(StringComparer.Ordinal);
    private bool _ownedLoaded;
    private int _ownedLoadFailures;
    private string _ownedLoadError = "";
    private long _nextOwnedLoadLogMs;
    private readonly object _ownedSaveLock = new();
    private bool _ownedDirty;
    // Explicitly unbound through Nexus this session; never re-seeded as owned.
    private readonly HashSet<string> _userUnbound = new(StringComparer.Ordinal);
    // Chains this hub unbound this session (user, slot conflict, adoption cap); one still reporting our master is unbound again.
    private readonly HashSet<string> _unboundHere = new(StringComparer.Ordinal);
    // L-Connect's cap on chains adopted from our master at first sight.
    internal const int AdoptionCap = 12;
    /// <summary>Refusal text for a manual Bind or Unbind while the lock is on.</summary>
    public const string LockedError = "locked";

    // L-Connect Bind caps (RFController.Bind): all devices, Strimers, and HydroShift IIs per dev_type.
    internal const int BindCapDevices = 10;
    internal const int BindCapStrimers = 3;
    internal const int BindCapHydroShiftPerType = 1;

    /// <summary>Reads the persisted lock setting; assigning it loads the value at once, and each tick re-reads it outside the hub lock.</summary>
    public Func<bool>? LockBoundDevices
    {
        get => _lockRead;
        set
        {
            _lockRead = value;
            RefreshLockSetting();
        }
    }

    private Func<bool>? _lockRead;
    private volatile bool _lockBound = true;
    // Set only while a poll or drive pass is sending: a timed-out write then ends that pass's sends. Every other caller always writes.
    private bool _pollSendsActive;
    private int _reconcileCursor;
    private bool _txStalledThisPoll;
    private readonly object _lockSettingGate = new();
    private int _lockVersion;

    /// <summary>Applies a lock change made through the route; a tick that read the setting earlier cannot overwrite it.</summary>
    public void SetLockBoundDevices(bool on)
    {
        lock (_lockSettingGate)
        {
            _lockVersion++;
            _lockBound = on;
            State.LockBoundDevices = on;
        }
    }

    // L-Connect bind_to_master: chains this hub steers to our master and the slot each is aimed at.
    private readonly Dictionary<string, BindIntent> _intents = new(StringComparer.Ordinal);
    // Raw wireless records of the latest answered poll, listed or not.
    private List<Slv3DeviceRecord> _pollRecords = new();
    private List<string> _reportOrder = new();
    // Master records (dev_type 0xFF) by master MAC hex, with the poll that last heard them.
    private readonly Dictionary<string, long> _liveMasters = new(StringComparer.Ordinal);
    internal const int LiveMasterPolls = 30;
    // Per-chain count of polls reporting its slot on another chain of ours (L-Connect ConflictCnt).
    internal const int SlotConflictLimit = 4;
    private const int SendLogInterval = 100;
    private readonly Dictionary<string, int> _conflictCounts = new(StringComparer.Ordinal);
    // The answered poll the conflict counters last saw; a busy reply does not advance it.
    private long _conflictPoll = -1;
    // Consecutive answered polls a bound owned chain has reported a slot other than its saved one, and the last poll counted.
    private readonly Dictionary<string, (int Slot, int Count, long Poll)> _ownedSlotSightings = new(StringComparer.Ordinal);
    internal Action<int> SleepMs { get; init; } = Thread.Sleep;

    private ISlv3Transport? _tx;
    private ISlv3Transport? _rx;
    private byte[] _masterMac = new byte[Slv3Protocol.MacLength];
    private byte _channel = Slv3Protocol.DefaultChannel;
    private bool _disposed;
    private bool _videoModeActive;
    private int _videoModePreppedCount;
    // Round-robin cursor into ChannelScanOrder so one connect attempt probes a
    // bounded slice; a full scan completes across successive attempts instead
    // of holding _lock for ~19 s of read timeouts in one call.
    private int _channelScanCursor;

    // Channel probes since the last answered GetMac. A silent dongle is only
    // reported as unresponsive once this has covered the whole scan order:
    // before that it may simply be parked on a channel no attempt reached yet.
    private int _probesSinceLastMac;

    // A chain unseen this long is treated as gone (powered off / out of range)
    // and dropped; until then it stays listed so downstream devices are stable.
    // Only time the RX was answering counts: a busy, failing or closed RX can
    // not see anyone, while the TX keeps driving the chains.
    private const long ChainExpiryMs = 30_000;
    // Unseen this long = telemetry is last-known, surfaced as Stale.
    private const long ChainStaleMs = 3_500;
    private long? _blindSinceMs;
    private long _blindTotalMs;
    // PWM frames to a chain whose record cannot confirm them (RX blind, or the
    // chain absent from the last list) repeat a target only this often.
    private const long UnconfirmedPwmResendMs = 5_000;
    private readonly Dictionary<string, (byte[] Pwm, long SentMs)> _unconfirmedPwm = new(StringComparer.Ordinal);

    // GetDev failure escalation (L-Connect MasterDevice.ResetRx): consecutive
    // RX failures -> UsbResetAnother written on the TX handle, which resets the
    // RX half (written on the RX it would reset the TX instead). Recoverable
    // faults never tear the link down; consecutive recoveries are spaced by a
    // doubling backoff, and a good reply clears it.
    private const int RxFailStreakForReset = 5;
    private const long RecoveryBackoffMs = 5_000;
    private const long RecoveryBackoffMaxMs = 60_000;
    private int _rxFailStreak;
    private int _rxRecoveries;
    private long _nextRxRecoveryMs;

    // A reply opening with 0 is the RX's "no device list this cycle". It does
    // this on its own every ~17 s for a few seconds and recovers without help,
    // so only a busy spell this long is treated as a wedge. A wedge gets a fresh
    // RX handle, never UsbResetAnother: the reset re-enumerates the RX, a Windows
    // device sound each time.
    private const int RxBusyPollsBeforeReset = 60;
    private int _rxBusyStreak;
    private byte[]? _firstBusyReply;

    // L-Connect MasterDevice.ResetTx: consecutive TX failures (a reopen or a
    // connect attempt that gets no master MAC) -> UsbResetAnother written on the
    // RX handle, then the TX keeps reopening. Only a master MAC from the TX
    // clears the streak and the backoff, so a link that never comes up still
    // escalates across connect attempts.
    private const int TxFailsBeforeReset = 5;
    private int _txFails;
    private int _txRecoveries;
    private long _nextTxRecoveryMs;

    // Last resort for a TX that ignores the RX reset: cycle the one hub port the TX
    // devnode sits on. It needs several RX resets sent and a minimum elapsed time since
    // the first, with the TX enumerated and never answering a probe in between. A TX
    // that recovers on its own does so on the first resets, long before either floor.
    internal const int TxResetsBeforePortCycle = 5;
    internal const long TxHangMinMsBeforePortCycle = 300_000;
    // Spacing between cycles, and a rolling cap that no answer resets: a TX that
    // answers once and hangs again must not be cycled forever. Past the cap only a
    // physical replug is left.
    internal const long PortCycleSpacingMs = 600_000;
    internal const int MaxPortCyclesPerWindow = 3;
    internal const long PortCycleWindowMs = 86_400_000;
    // No cycle until this long after a resume: the TX is re-enumerating on its own.
    internal const long PortCycleResumeQuietMs = 120_000;
    // Modern Standby may raise no suspend or resume event. The worker passes every few
    // seconds, so a longer gap between two passes means the process was frozen and the
    // same quiet period applies.
    internal const long PortCyclePassGapMs = 90_000;
    private readonly Queue<long> _portCycleTimesMs = new();
    private bool _portCycleCapLogged;
    private bool _portCycleQuietLogged;
    private long _lastCyclePassMs;
    private long _gapQuietUntilMs;
    private int _txHungResets;
    private long _txHungSinceMs;
    private int _portCycles;
    private long _nextPortCycleMs;
    private int _portCycleInFlight;
    private long _powerQuietUntilMs;

    /// <summary>Windows only; null leaves the TX reset to the RX alone.</summary>
    internal ISlv3TxPortCycler? TxPortCycler { get; set; }

    internal Action<Action> RunPortCycle { get; init; } = work => _ = Task.Run(work);

    // The transport keeps a TX handle open through write timeouts, so a TX that
    // sends nothing back to the once-a-second GetMac this many times in a row is
    // closed here; the reopen path then counts toward the reset above.
    private const int TxMissedProbesBeforeReopen = 5;
    private int _txMissedProbes;
    private int _txProbeWriteFailures;

    // Neither dongle enumerating this long means it was unplugged: the only
    // fault that ends the connection.
    internal const long LinkAbsentDisconnectMs = 10_000;
    private long _bothAbsentSinceMs;

    // A half that stays unenumerated this long is surfaced as rxMissing / txMissing;
    // the link and the device list are kept.
    internal const long HalfMissingStatusMs = 30_000;
    private long _rxAbsentSinceMs;
    private long _txAbsentSinceMs;
    private long _nextRxOpenMs;
    private int _rxOpenFailures;

    // L-Connect broadcasts SaveCfg on every loop pass for this long after a user Bind.
    internal const long PostBindSaveCfgMs = 5_000;
    private long? _lastBindMs;

    // CheckSaveConfig: the first periodic save once the hub has run this long, then one per interval since the last save.
    internal const long PeriodicFirstSaveMs = 3_600_000;
    internal const long PeriodicSaveMs = 3 * 3_600_000;
    internal const long PeriodicEffectQuietMs = 30_000;
    private long _periodicBaseMs;
    private long _lastSaveMs;
    private bool _firstPeriodicSaveDone;

    // Throttled SaveCfg after any binding or effect change (RFController.SaveConfig):
    // fires SaveCfgDelayMs after the first change, deferred by the same delay
    // while the last change is younger than SaveCfgQuietMs. A live RGB stream
    // never settles, so it saves once when the stream goes quiet.
    private const long SaveCfgDelayMs = 10_000;
    private const long SaveCfgQuietMs = 5_000;
    private long _saveCfgDueMs;
    private long _lastConfigChangeMs;

    // Sends a sequenced control frame (RF_Select / RF_RebootLcd) gets before it
    // is dropped without an echo (L-Connect selected / reboot_lcd counters).
    internal const int SequencedCommandBudget = 10;

    // Header packet repeats for an RGB upload, the only profile L-Connect uses
    // (no CRC on this link; a lost header sticks until the effect_index echo
    // shows it).
    private const int RgbHeaderRepeats = 4;
    private const int RgbHeaderGapMs = 20;

    // Rolling-window uploads. The chain pauses playback while an upload
    // addressed to it is in flight, so the headers go back to back under one
    // lock. The link drops data packets and never retransmits, so each data
    // part goes out twice in the upload and again in data-only late passes; a
    // late header would re-pause playback.
    private const int WindowHeaderRepeats = 8;
    private const int WindowDataPasses = 2;
    private const int WindowLatePasses = 2;
    private const int WindowLateGapMs = 60;

    // RF payloads per second the late passes may bring the TX up to. The TX
    // accepts writes far faster than the air carries them and drops the excess:
    // two chains sustaining 258/s lost every window, 193/s landed (Y70 USBPcap,
    // 2026-10-04).
    private const int LatePassAirBudgetPerSecond = 180;
    private const int AirRateWindowMs = 1000;
    private readonly Queue<long> _airSendsMs = new();

    // Seeded so a restart does not replay the index a chain last reported.
    private byte _effectCounter = (byte)Environment.TickCount64;

    // Device-list records span more than one page once enough chains are bound
    // (up to MaxSlot, plus non-fan devices), so the poll requests
    // ceil(count/RecordsPerPage) pages. Clamp so a corrupt count can't trigger
    // a runaway read.
    private const int MaxDeviceListPages = 3;
    private int _lastRecordCount;

    public Slv3Hub(
        ISlv3Discovery discovery,
        Func<Slv3PortInfo, ISlv3Transport> transportFactory,
        Func<long>? nowMs = null,
        Nexus.Service.Lighting.LcdLockState? lockState = null)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        if (lockState is not null)
        {
            _lockSleep = new Nexus.Service.Lighting.LcdLockSleepSet(lockState);
        }
    }

    // Session lock. The HydroShift II screen's backlight rides in the param block that goes
    // out every drive tick, so the dark value is written into that block and nothing else is
    // sent: no extra frames, no timing or link change. Decided once per lock per AIO.
    private readonly Nexus.Service.Lighting.LcdLockSleepSet? _lockSleep;

    // Monotonic ms clock; injectable so tests drive chain expiry deterministically.
    private readonly Func<long> _nowMs;

    public Slv3State State { get; } = new();

    /// <summary>
    /// The link is up. It stays up while a dongle half is reset or reopened in
    /// place; only a replug (neither half enumerating) or <see cref="Disconnect"/> ends it.
    /// </summary>
    public bool IsConnected => State.IsConnected;

    /// <summary>Opens the TX + RX dongles and learns our master MAC. Both must open for the link to be usable.</summary>
    public bool EnsureConnected()
    {
        if (_disposed)
        {
            return false;
        }
        if (IsConnected)
        {
            return true;
        }
        RefreshLockSetting();
        lock (_lock)
        {
            if (IsConnected)
            {
                return true;
            }

            Slv3PortInfo? txPort = null;
            Slv3PortInfo? rxPort = null;
            foreach (var port in _discovery.Discover())
            {
                if (port.Role == Slv3DongleRole.Tx)
                {
                    txPort ??= port;
                }
                else if (port.Role == Slv3DongleRole.Rx)
                {
                    rxPort ??= port;
                }
            }
            ConsiderTxPortCycleLocked(txPort, _nowMs());
            if (txPort is null || rxPort is null)
            {
                // The module presents TX and RX as two WinUSB devices behind its
                // own hub, so one half missing is a real state (a failed
                // enumeration on that port), not "nothing plugged in".
                State.LinkStatus = txPort is null && rxPort is null ? Slv3LinkStatus.None
                    : txPort is null ? Slv3LinkStatus.TxMissing
                    : Slv3LinkStatus.RxMissing;
                // A reset via the RX re-enumerates only the TX, so only a module
                // with neither half listed for LinkAbsentDisconnectMs was
                // unplugged and starts a fresh streak.
                var nowMs = _nowMs();
                if (txPort is not null || rxPort is not null)
                {
                    _bothAbsentSinceMs = 0;
                }
                else if (_bothAbsentSinceMs == 0)
                {
                    _bothAbsentSinceMs = nowMs;
                }
                else if (nowMs - _bothAbsentSinceMs >= LinkAbsentDisconnectMs)
                {
                    _txFails = 0;
                    _txRecoveries = 0;
                    _nextTxRecoveryMs = 0;
                    ClearPortCycleEpisodeLocked();
                }
                return false;
            }
            _bothAbsentSinceMs = 0;

            try
            {
                _tx = _transportFactory(txPort);
                _rx = _transportFactory(rxPort);
            }
            catch (Exception ex)
            {
                State.LinkStatus = ex is Slv3OpenException { IsInUseByAnotherApp: true }
                    ? Slv3LinkStatus.Busy
                    : Slv3LinkStatus.OpenFailed;
                ServiceLog.Error($"[lianli-wireless] open failed: {ex.GetType().Name}: {ex.Message}");
                DisconnectLocked();
                return false;
            }

            if (!MasterInitLocked(out var anyWritten))
            {
                State.LinkStatus = _probesSinceLastMac >= Slv3Protocol.ChannelScanOrder().Length
                    ? Slv3LinkStatus.NoResponse
                    : Slv3LinkStatus.Unknown;
                ServiceLog.Warn($"[lianli-wireless] GetMac failed on connect{(anyWritten ? "" : " (every write failed)")}");
                // A TX parked on another channel still takes writes, and a hard write
                // error closes the handle of a TX that is re-enumerating, so writes
                // that all timed out on a handle still open are a hung TX: the first
                // reset of a streak goes out now instead of after TxFailsBeforeReset
                // attempts.
                if (!anyWritten && _tx is { IsOpen: true } && _txRecoveries == 0)
                {
                    ResetTxViaRxLocked(_nowMs(), "every GetMac write on connect failed");
                }
                else
                {
                    NoteTxFailureLocked(_nowMs());
                }
                DisconnectLocked();
                return false;
            }

            State.LinkStatus = Slv3LinkStatus.Ok;
            State.IsConnected = true;
            ServiceLog.Info($"[lianli-wireless] connected (tx={txPort.PortName}, rx={rxPort.PortName}, master={State.MasterMac})");
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            DisconnectLocked();
        }
    }

    private void DisconnectLocked()
    {
        try { _tx?.Dispose(); } catch { /* best effort */ }
        try { _rx?.Dispose(); } catch { /* best effort */ }
        _tx = null;
        _rx = null;
        State.IsConnected = false;
        // A failure path sets its own reason just before tearing down; only an
        // 'ok' from a link that was live outlives its meaning here.
        if (State.LinkStatus is Slv3LinkStatus.Ok or Slv3LinkStatus.Recovering)
        {
            State.LinkStatus = Slv3LinkStatus.Unknown;
        }
        State.Fans = Array.Empty<Slv3FanInfo>();
        State.MotherboardPwmPercent = null;
        _lastFanRecords = new List<Slv3DeviceRecord>();
        _anyAioBound = false;
        _knownChains.Clear();
        _unconfirmedChains.Clear();
        _bindingLogs.Clear();
        _intents.Clear();
        _unboundHere.Clear();
        _pollRecords = new List<Slv3DeviceRecord>();
        _liveMasters.Clear();
        _conflictCounts.Clear();
        _conflictPoll = -1;
        _ownedSlotSightings.Clear();
        _pendingCommands.Clear();
        _deferredUnbinds.Clear();
        _lastIssuedSeq.Clear();
        _rxAbsentSinceMs = 0;
        _txAbsentSinceMs = 0;
        _nextRxOpenMs = 0;
        _rxOpenFailures = 0;
        _blindSinceMs = null;
        _blindTotalMs = 0;
        _unconfirmedPwm.Clear();
        foreach (var control in _aioControl.Values)
        {
            control.Switched = false;
            control.SwitchSeq = 0;
        }
        _rxFailStreak = 0;
        _rxBusyStreak = 0;
        _rxRecoveries = 0;
        _nextRxRecoveryMs = 0;
        _txMissedProbes = 0;
        _txProbeWriteFailures = 0;
        _bothAbsentSinceMs = 0;
        _lastBindMs = null;
        _saveCfgDueMs = 0;
        _videoModeActive = false;
        _videoModePreppedCount = 0;
    }

    // Reopens whichever half lost its handle, keeping the device list, pending
    // ops and owned state. Returns false only once neither dongle has enumerated
    // for LinkAbsentDisconnectMs.
    private bool RecoverHandlesLocked(long nowMs)
    {
        Slv3PortInfo? txPort = null;
        Slv3PortInfo? rxPort = null;
        foreach (var port in _discovery.Discover())
        {
            if (port.Role == Slv3DongleRole.Tx)
            {
                txPort ??= port;
            }
            else if (port.Role == Slv3DongleRole.Rx)
            {
                rxPort ??= port;
            }
        }
        ConsiderTxPortCycleLocked(txPort, nowMs);
        if (txPort is null && rxPort is null)
        {
            if (_bothAbsentSinceMs == 0)
            {
                _bothAbsentSinceMs = nowMs;
            }
            if (nowMs - _bothAbsentSinceMs >= LinkAbsentDisconnectMs)
            {
                ServiceLog.Warn($"[lianli-wireless] neither dongle enumerated for {LinkAbsentDisconnectMs / 1000}s, link down");
                // A replugged module brings a fresh TX; its failures start over.
                _txFails = 0;
                _txRecoveries = 0;
                _nextTxRecoveryMs = 0;
                ClearPortCycleEpisodeLocked();
                return false;
            }
        }
        else
        {
            _bothAbsentSinceMs = 0;
        }
        if (_tx is not { IsOpen: true })
        {
            ReopenTxLocked(txPort, nowMs);
        }
        if (_rx is not { IsOpen: true })
        {
            ReopenRxLocked(rxPort);
        }
        _txAbsentSinceMs = _tx is { IsOpen: true } || txPort is not null ? 0 : _txAbsentSinceMs == 0 ? nowMs : _txAbsentSinceMs;
        _rxAbsentSinceMs = _rx is { IsOpen: true } || rxPort is not null ? 0 : _rxAbsentSinceMs == 0 ? nowMs : _rxAbsentSinceMs;
        State.LinkStatus = _rxAbsentSinceMs != 0 && nowMs - _rxAbsentSinceMs >= HalfMissingStatusMs ? Slv3LinkStatus.RxMissing
            : _txAbsentSinceMs != 0 && nowMs - _txAbsentSinceMs >= HalfMissingStatusMs ? Slv3LinkStatus.TxMissing
            : Slv3LinkStatus.Recovering;
        return true;
    }

    // The new handle must answer GetMac: a dying instance can still be listed for a moment.
    private void ReopenTxLocked(Slv3PortInfo? txPort, long nowMs)
    {
        try { _tx?.Dispose(); } catch { /* best effort */ }
        _tx = null;
        if (txPort is null)
        {
            NoteTxFailureLocked(nowMs);
            return;
        }
        try
        {
            _tx = _transportFactory(txPort);
        }
        catch (Exception)
        {
            NoteTxFailureLocked(nowMs);
            return;
        }
        if (!TryGetMacOnChannelLocked(_channel))
        {
            try { _tx.Dispose(); } catch { /* best effort */ }
            _tx = null;
            NoteTxFailureLocked(nowMs);
            return;
        }
        // The restarted TX lost the LCD video arming; the LCD loop re-arms it.
        _videoModeActive = false;
        _videoModePreppedCount = 0;
        ServiceLog.Info("[lianli-wireless] TX reopened");
    }

    // L-Connect ResetTx: UsbResetAnother on the RX handle restarts the TX.
    private void NoteTxFailureLocked(long nowMs)
    {
        if (++_txFails >= TxFailsBeforeReset)
        {
            ResetTxViaRxLocked(nowMs, $"{TxFailsBeforeReset} consecutive TX failures");
        }
    }

    private void ResetTxViaRxLocked(long nowMs, string reason)
    {
        // The port cycle owns the TX while it runs.
        if (nowMs < _nextTxRecoveryMs || Volatile.Read(ref _portCycleInFlight) != 0)
        {
            return;
        }
        _txFails = 0;
        // An unsent reset still waits out the backoff but is not counted, so the
        // first-attempt reset stays available and "answered after" counts only
        // resets that went out.
        if (_rx is not { IsOpen: true } || !_rx.RfSend(Slv3Protocol.BuildResetAnother()))
        {
            _nextTxRecoveryMs = nowMs + RecoveryBackoffFor(_txRecoveries + 1);
            ServiceLog.Warn($"[lianli-wireless] {reason}, but the TX reset could not be written to the RX");
            return;
        }
        _txRecoveries++;
        if (_txHungResets++ == 0)
        {
            _txHungSinceMs = nowMs;
        }
        _nextTxRecoveryMs = nowMs + RecoveryBackoffFor(_txRecoveries);
        ServiceLog.Warn($"[lianli-wireless] {reason}, resetting TX via RX ({_txRecoveries})");
    }

    private static long RecoveryBackoffFor(int recoveries) =>
        Math.Min(RecoveryBackoffMs << Math.Min(recoveries - 1, 4), RecoveryBackoffMaxMs);

    private void ClearPortCycleEpisodeLocked()
    {
        _txHungResets = 0;
        _txHungSinceMs = 0;
        _portCycles = 0;
    }

    // Called with the TX this pass found enumerated, or null when it is not. A TX
    // that left the bus was re-enumerated, which is not a hang the RX reset ignored.
    private void ConsiderTxPortCycleLocked(Slv3PortInfo? txPort, long nowMs)
    {
        var passGap = _lastCyclePassMs != 0 && nowMs - _lastCyclePassMs > PortCyclePassGapMs;
        _lastCyclePassMs = nowMs;
        if (passGap)
        {
            _gapQuietUntilMs = nowMs + PortCycleResumeQuietMs;
        }
        if (txPort is null)
        {
            _txHungResets = 0;
            _txHungSinceMs = 0;
            return;
        }
        var cycler = TxPortCycler;
        if (cycler is null || _disposed || txPort.Role != Slv3DongleRole.Tx
            || _txHungResets < TxResetsBeforePortCycle
            || nowMs - _txHungSinceMs < TxHangMinMsBeforePortCycle
            || nowMs < _nextPortCycleMs
            || Volatile.Read(ref _portCycleInFlight) != 0)
        {
            return;
        }
        var powerQuietUntil = Volatile.Read(ref _powerQuietUntilMs);
        if (nowMs < powerQuietUntil || nowMs < _gapQuietUntilMs)
        {
            if (!_portCycleQuietLogged)
            {
                _portCycleQuietLogged = true;
                ServiceLog.Info(powerQuietUntil == long.MaxValue
                    ? "[lianli-wireless] TX port cycle held back: suspend seen without a resume"
                    : "[lianli-wireless] TX port cycle held back: recent power transition");
            }
            return;
        }
        _portCycleQuietLogged = false;
        while (_portCycleTimesMs.Count > 0 && nowMs - _portCycleTimesMs.Peek() >= PortCycleWindowMs)
        {
            _portCycleTimesMs.Dequeue();
        }
        if (_portCycleTimesMs.Count >= MaxPortCyclesPerWindow)
        {
            if (!_portCycleCapLogged)
            {
                _portCycleCapLogged = true;
                ServiceLog.Warn($"[lianli-wireless] TX still hung after {_portCycleTimesMs.Count} port cycle(s) in the last day, giving up: unplug and replug the module");
            }
            return;
        }
        _portCycleCapLogged = false;
        _portCycleTimesMs.Enqueue(nowMs);
        _portCycles++;
        var episode = _portCycleTimesMs.Count;
        var resets = _txHungResets;
        var hungMinutes = (nowMs - _txHungSinceMs) / 60_000;
        _nextPortCycleMs = nowMs + PortCycleSpacingMs;
        _txHungResets = 0;
        _txHungSinceMs = 0;
        Volatile.Write(ref _portCycleInFlight, 1);
        RunPortCycle(() => CycleTxPort(cycler, txPort, episode, resets, hungMinutes));
    }

    // Runs without _lock: the hub IOCTL and the re-arrival wait can block for seconds.
    private void CycleTxPort(ISlv3TxPortCycler cycler, Slv3PortInfo txPort, int episode, int resets, long hungMinutes)
    {
        try
        {
            if (!cycler.TryResolve(txPort, out var instanceId, out var target))
            {
                ServiceLog.Warn($"[lianli-wireless] TX port cycle refused: {target}");
                return;
            }
            if (_disposed || _nowMs() < Volatile.Read(ref _powerQuietUntilMs))
            {
                return;
            }
            ServiceLog.Warn($"[lianli-wireless] TX ignored {resets} RX reset(s) over {hungMinutes} min, cycling its USB port {target} ({episode}/{MaxPortCyclesPerWindow} today)");
            var ok = cycler.TryCycle(instanceId, out var detail);
            ServiceLog.Warn($"[lianli-wireless] TX port cycle {(ok ? "done" : "failed")}: {detail}");
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] TX port cycle failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _portCycleInFlight, 0);
        }
    }

    /// <summary>The system resumed: no TX port cycle until it has had time to re-enumerate on its own.</summary>
    public void OnSystemResumed() =>
        Volatile.Write(ref _powerQuietUntilMs, _nowMs() + PortCycleResumeQuietMs);

    private bool MasterInitLocked(out bool anyWritten)
    {
        anyWritten = false;
        if (_tx is null)
        {
            return false;
        }
        // Probe the configured channel first, then a bounded slice of the scan
        // order: a dongle left on another channel by L-Connect only answers
        // GetMac there, and a zero MAC in the reply means "no master on this
        // channel", not success. Each dead probe costs a full 500 ms read
        // timeout under _lock, so one attempt probes at most ScanProbesPerAttempt
        // channels; the cursor resumes there on the worker's next 5 s retry,
        // covering all 39 channels across a few attempts without starving the
        // routes and writer that share the lock.
        _probesSinceLastMac++;
        if (TryGetMacOnChannelLocked(_channel, out var probe))
        {
            _probesSinceLastMac = 0;
            return true;
        }
        anyWritten = probe != TxProbe.WriteFailed;
        const int ScanProbesPerAttempt = 8;
        var order = Slv3Protocol.ChannelScanOrder();
        for (var probes = 0; probes < ScanProbesPerAttempt && probes < order.Length; _channelScanCursor++)
        {
            var channel = order[_channelScanCursor % order.Length];
            if (channel == _channel)
            {
                continue;
            }
            probes++;
            _probesSinceLastMac++;
            if (TryGetMacOnChannelLocked(channel, out probe))
            {
                ServiceLog.Info($"[lianli-wireless] master found on channel {channel} (scanned from {_channel})");
                _channel = channel;
                State.Channel = channel;
                _probesSinceLastMac = 0;
                return true;
            }
            anyWritten |= probe != TxProbe.WriteFailed;
        }
        return false;
    }

    private enum TxProbe
    {
        WriteFailed,
        NoReply,
        // A GetMac reply came back, with or without a master MAC.
        Answered,
    }

    private bool TryGetMacOnChannelLocked(byte channel) => TryGetMacOnChannelLocked(channel, out _);

    private bool TryGetMacOnChannelLocked(byte channel, out TxProbe probe)
    {
        if (_tx is null || !_tx.RfSend(Slv3Protocol.BuildGetMac(channel)))
        {
            probe = TxProbe.WriteFailed;
            return false;
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        if (!Slv3Protocol.TryParseGetMac(reply, out var mac, out _, out var fw))
        {
            probe = TxProbe.NoReply;
            return false;
        }
        probe = TxProbe.Answered;
        // A reply of any kind means the TX is not hung, whatever it says about the master.
        _txHungResets = 0;
        _txHungSinceMs = 0;
        if (Slv3Protocol.MacIsZero(mac))
        {
            return false;
        }
        if (_portCycles > 0)
        {
            ServiceLog.Info($"[lianli-wireless] TX answered after port cycle ({_portCycles}), {_txRecoveries} reset(s) via RX");
        }
        else if (_txRecoveries > 0)
        {
            ServiceLog.Info($"[lianli-wireless] TX answered after {_txRecoveries} reset(s) via RX");
        }
        _txFails = 0;
        _txRecoveries = 0;
        _nextTxRecoveryMs = 0;
        ClearPortCycleEpisodeLocked();
        _txMissedProbes = 0;
        _txProbeWriteFailures = 0;
        if (!Slv3Protocol.MacIsZero(_masterMac) && !Slv3Protocol.MacEquals(_masterMac, mac))
        {
            ServiceLog.Warn($"[lianli-wireless] dongle master MAC {Convert.ToHexString(_masterMac)} -> {Convert.ToHexString(mac)} on channel {channel}");
        }
        _masterMac = mac;
        State.MasterMac = Convert.ToHexString(mac);
        State.TxFirmwareVersion = fw;
        State.Channel = channel;
        return true;
    }

    /// <summary>
    /// The per-poll pass (L-Connect RefreshList + SyncControlInfo): refresh the
    /// device list, reconcile every kept-bound chain against the fresh
    /// report, and re-send pending select/reboot commands. Returns false only when
    /// the link is down; a faulty dongle half is recovered in place.
    /// </summary>
    public bool PollTick()
    {
        _usbAioMac = ReadUsbAioMac();
        RefreshLockSetting();
        EnsureOwnedLoaded();
        try
        {
            lock (_lock)
            {
                return RunPassLocked(PollLocked);
            }
        }
        finally
        {
            FlushOwned();
        }
    }

    /// <summary>
    /// The once-a-second pass: everything <see cref="PollTick"/> does, then
    /// L-Connect's SyncPwm (a bind/PWM frame only for a chain whose reported
    /// duty drifted from its target), QuerryMasterMac, SyncMasterClock, and
    /// the SaveCfg burst/throttle.
    /// </summary>
    public bool DriveTick()
    {
        _usbAioMac = ReadUsbAioMac();
        RefreshLockSetting();
        var aio = _anyAioControlled || _anyAioBound;
        var aioSensors = aio ? ReadAioSensors() : default;
        var aioScreens = aio ? ReadAioScreens() : null;
        var flexLcd = ReadFlexLcd();
        EnsureOwnedLoaded();
        try
        {
            return DriveLocked(aioSensors, aioScreens, flexLcd);
        }
        finally
        {
            FlushOwned();
        }
    }

    private bool DriveLocked(Slv3AioSensors aioSensors, IReadOnlyDictionary<string, Slv3AioScreen>? aioScreens, Slv3FlexLcdFrame? flexLcd)
    {
        lock (_lock)
        {
            return RunPassLocked(() =>
            {
                if (!PollLocked())
                {
                    return false;
                }
                if (_tx is not { IsOpen: true })
                {
                    return true;
                }
                SyncPwmLocked();
                SyncAioLocked(aioSensors, aioScreens);
                RefreshMasterMacLocked();
                SendClockHeartbeatLocked(SyncFlexLcdLocked(flexLcd, _nowMs()));
                RunSaveCfgScheduleLocked();
                return true;
            });
        }
    }

    // Caller holds _lock.
    private bool RunPassLocked(Func<bool> pass)
    {
        _pollSendsActive = true;
        _txStalledThisPoll = false;
        try
        {
            return pass();
        }
        finally
        {
            _pollSendsActive = false;
            _txStalledThisPoll = false;
        }
    }

    private bool PollLocked()
    {
        if (!IsConnected)
        {
            return false;
        }
        var nowMs = _nowMs();
        if ((_tx is not { IsOpen: true } || _rx is not { IsOpen: true }) && !RecoverHandlesLocked(nowMs))
        {
            return false;
        }
        if (_rx is not { IsOpen: true })
        {
            AgeChainsLocked(nowMs);
            return true;
        }
        if (!RefreshDeviceListLocked())
        {
            return false;
        }

        // Pending commands keep their budgets while the TX is away instead of
        // spending them on sends that cannot go out.
        if (_tx is not { IsOpen: true })
        {
            return true;
        }
        ProcessDeferredUnbindsLocked(nowMs);
        ReconcileLocked();
        TrackOwnedLocked();
        ResolveSlotConflictsLocked();
        SyncControlLocked();
        return true;
    }

    private void EnsureOwnedLoaded()
    {
        if (_ownedLoaded)
        {
            return;
        }
        IReadOnlyDictionary<string, int>? saved = null;
        if (OwnedDevicesLoad is { } load)
        {
            try
            {
                saved = load();
            }
            catch (Exception ex)
            {
                // Not marked loaded, so the next poll retries and no save overwrites the persisted list.
                var nowMs = _nowMs();
                if (_ownedLoadFailures++ == 0 || ex.Message != _ownedLoadError || nowMs >= _nextOwnedLoadLogMs)
                {
                    ServiceLog.Warn($"[lianli-wireless] owned device list read failed ({_ownedLoadFailures}): {ex.Message}");
                    _ownedLoadError = ex.Message;
                    _nextOwnedLoadLogMs = nowMs + RecoveryBackoffFor(_ownedLoadFailures);
                }
                return;
            }
        }
        lock (_lock)
        {
            if (_ownedLoaded)
            {
                return;
            }
            if (saved is not null)
            {
                foreach (var (mac, slot) in saved)
                {
                    var key = mac.ToUpperInvariant();
                    if (!_userUnbound.Contains(key))
                    {
                        _owned.TryAdd(key, slot);
                    }
                }
            }
            _ownedLoaded = true;
            _ownedLoadFailures = 0;
            _ownedDirty |= saved is not null && _owned.Count != saved.Count;
        }
    }

    // The save lock spans snapshot and save, so a stale snapshot can never overwrite a newer one.
    private void FlushOwned()
    {
        lock (_ownedSaveLock)
        {
            Dictionary<string, int> snapshot;
            lock (_lock)
            {
                if (!_ownedDirty || !_ownedLoaded)
                {
                    return;
                }
                _ownedDirty = false;
                snapshot = new Dictionary<string, int>(_owned, StringComparer.Ordinal);
            }
            try
            {
                OwnedDevicesSave?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _ownedDirty = true;
                }
                ServiceLog.Warn($"[lianli-wireless] owned device list save failed: {ex.Message}");
            }
        }
    }

    // A listed chain seen bound to our master is ours, which also seeds the list on upgrade.
    private void TrackOwnedLocked()
    {
        foreach (var record in _lastFanRecords)
        {
            if (!IsBoundToUsLocked(record))
            {
                continue;
            }
            var key = Convert.ToHexString(record.Mac);
            if (_userUnbound.Contains(key) || (_intents.TryGetValue(key, out var intent) && intent.Unbinding))
            {
                continue;
            }
            if (_owned.TryGetValue(key, out var slot))
            {
                if (slot == record.RxType || SlotSharedLocked(record))
                {
                    _ownedSlotSightings.Remove(key);
                    continue;
                }
                if (!_knownChains.TryGetValue(key, out var chain) || chain.Poll != _devicePolls)
                {
                    _ownedSlotSightings.Remove(key);
                    continue;
                }
                var sameSlot = _ownedSlotSightings.TryGetValue(key, out var prior) && prior.Slot == record.RxType;
                if (sameSlot && chain.Poll <= prior.Poll)
                {
                    continue;
                }
                var seen = sameSlot ? prior.Count + 1 : 1;
                if (seen < ChainConfirmSightings)
                {
                    _ownedSlotSightings[key] = (record.RxType, seen, chain.Poll);
                    continue;
                }
                _ownedSlotSightings.Remove(key);
            }
            _owned[key] = record.RxType;
            _ownedDirty = true;
        }
    }

    // Two chains bound to us reporting one slot: the owned slot is kept until the conflict resolves.
    private bool SlotSharedLocked(Slv3DeviceRecord record)
    {
        foreach (var other in _lastFanRecords)
        {
            if (IsBoundToUsLocked(other) && other.RxType == record.RxType && !Slv3Protocol.MacEquals(other.Mac, record.Mac))
            {
                return true;
            }
        }
        return false;
    }

    // A live master is one heard in the last LiveMasterPolls answered polls; ours never counts. Manual Bind and Unbind consult it, and a pending unbind is dropped once one holds the fan.
    private bool IsLiveMasterLocked(byte[] master)
    {
        return !Slv3Protocol.MacIsZero(master) && !Slv3Protocol.MacEquals(master, _masterMac)
            && _liveMasters.TryGetValue(Convert.ToHexString(master, 0, Slv3Protocol.MacLength), out var poll)
            && _devicePolls - poll < LiveMasterPolls;
    }

    // L-Connect SyncControlInfo: with the lock on, every poll each chain this hub keeps bound (bind_to_master) whose
    // reported slot, channel or master differs from its target gets one bind frame at the pipe it reports.
    // With the lock off no chain is steered back; only an operation started here (manual Bind, Unbind, slot-conflict unbind, or a channel-only move of a chain already ours) is carried on.
    private void ReconcileLocked()
    {
        if (Slv3Protocol.MacIsZero(_masterMac))
        {
            return;
        }
        var merged = new Dictionary<string, Slv3DeviceRecord>(StringComparer.Ordinal);
        foreach (var record in _lastFanRecords)
        {
            if (!IsWiredOwned(Convert.ToHexString(record.Mac)) || (IsBoundToUsLocked(record) && !_userUnbound.Contains(Convert.ToHexString(record.Mac))) || IsReleasingLocked(Convert.ToHexString(record.Mac)))
            {
                merged[Convert.ToHexString(record.Mac)] = record;
            }
        }
        foreach (var record in _pollRecords)
        {
            var key = Convert.ToHexString(record.Mac);
            if ((!IsWiredOwned(key) || (IsBoundToUsLocked(record) && !_userUnbound.Contains(key)) || IsReleasingLocked(key)) && (merged.ContainsKey(key) || _intents.ContainsKey(key) || _owned.ContainsKey(key)))
            {
                merged[key] = record;
            }
        }
        // Adoption is deterministic: owned chains first, then newcomers in the order the RX reported them.
        var adoptionOrder = new List<string>(merged.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sorted = new List<string>(merged.Keys);
        sorted.Sort(StringComparer.Ordinal);
        foreach (var key in sorted)
        {
            if (_owned.ContainsKey(key) && seen.Add(key))
            {
                adoptionOrder.Add(key);
            }
        }
        foreach (var record in _pollRecords)
        {
            var key = Convert.ToHexString(record.Mac);
            if (merged.ContainsKey(key) && seen.Add(key))
            {
                adoptionOrder.Add(key);
            }
        }
        foreach (var key in sorted)
        {
            if (seen.Add(key))
            {
                adoptionOrder.Add(key);
            }
        }
        foreach (var key in adoptionOrder)
        {
            if (!_intents.TryGetValue(key, out var existing))
            {
                AdoptLocked(key, merged[key], merged);
            }
            else if (_lockBound && existing.Unbinding && _owned.ContainsKey(key) && !_userUnbound.Contains(key))
            {
                // Locking back on turns this hub's own unbinds (slot conflict, adoption cap) on owned chains back into binds.
                _unboundHere.Remove(key);
                _intents[key] = new BindIntent(OwnedSlotOrUnusedLocked(key));
            }
        }

        // The send pass starts one chain further along each poll, so a slow TX cannot starve the later chains.
        var count = sorted.Count;
        var first = count == 0 ? 0 : _reconcileCursor++ % count;
        for (var i = 0; i < count; i++)
        {
            var key = sorted[(first + i) % count];
            if (!_intents.TryGetValue(key, out var intent))
            {
                continue;
            }
            var record = merged[key];
            if (intent.Unbinding)
            {
                ReconcileUnbindLocked(key, intent, record);
            }
            else
            {
                ReconcileBindLocked(key, intent, record);
            }
        }
    }

    // L-Connect adoption: with the lock on, a chain reporting our master is steered from its first sight and an
    // owned chain is steered whatever it reports. A chain this hub unbound that still reports our master is cleared again.
    private void AdoptLocked(string key, Slv3DeviceRecord record, Dictionary<string, Slv3DeviceRecord> merged)
    {
        var ours = Slv3Protocol.MacEquals(record.MasterMac, _masterMac);
        BindIntent intent;
        if (_userUnbound.Contains(key))
        {
            if (!ours)
            {
                return;
            }
            intent = new BindIntent(0) { Unbinding = true };
        }
        else if (_lockBound && _owned.ContainsKey(key))
        {
            _unboundHere.Remove(key);
            var reported = ours && ValidSlot(record.RxType) ? record.RxType : (byte)0;
            intent = new BindIntent(reported != 0 && (!SlotHeldByOtherLocked(reported, key, merged) || (_owned[key] == reported && !IntentAimedAtLocked(reported, key)))
                ? reported
                : OwnedSlotOrUnusedLocked(key));
        }
        else if (ours && _unboundHere.Contains(key))
        {
            intent = new BindIntent(0) { Unbinding = true };
        }
        else if (ours && _knownChains.ContainsKey(key))
        {
            if (!_lockBound)
            {
                if (!ValidSlot(record.RxType) || record.Channel == _channel)
                {
                    return;
                }
                intent = new BindIntent(record.RxType);
            }
            else if (AdoptionCapReachedLocked())
            {
                intent = new BindIntent(0) { Unbinding = true };
                _unboundHere.Add(key);
            }
            else
            {
                intent = new BindIntent(ValidSlot(record.RxType) && !SlotHeldByOtherLocked(record.RxType, key, merged)
                    ? record.RxType
                    : GetRxUnusedLocked());
            }
        }
        else
        {
            return;
        }
        _intents[key] = intent;
    }

    // A slot another chain is aimed at or reports on our master; merged also holds owned chains not yet listed.
    private bool SlotHeldByOtherLocked(byte slot, string key, Dictionary<string, Slv3DeviceRecord> merged)
    {
        if (RxClaimedLocked(slot, key))
        {
            return true;
        }
        foreach (var (otherKey, other) in merged)
        {
            if (otherKey != key && other.RxType == slot && IsBoundToUsLocked(other))
            {
                return true;
            }
        }
        return false;
    }

    private bool AdoptionCapReachedLocked()
    {
        var kept = 0;
        foreach (var intent in _intents.Values)
        {
            kept += intent.Unbinding ? 0 : 1;
        }
        return kept >= AdoptionCap;
    }

    private static bool ValidSlot(int slot) => Slv3Protocol.IsBoundSlot(slot);

    // The slot a chain last held when it is free, else L-Connect's GetRxUnused.
    private byte OwnedSlotOrUnusedLocked(string key)
    {
        if (_owned.TryGetValue(key, out var slot) && ValidSlot(slot) && !RxClaimedLocked((byte)slot, key))
        {
            return (byte)slot;
        }
        return GetRxUnusedLocked();
    }

    private void ReconcileUnbindLocked(string key, BindIntent intent, Slv3DeviceRecord record)
    {
        if (Slv3Protocol.MacIsZero(record.MasterMac) || IsLiveMasterLocked(record.MasterMac))
        {
            _intents.Remove(key);
            return;
        }
        SendBindFrameLocked(record, 0, unbind: true);
        NoteSendLocked(key, intent, "unbind", record);
    }

    private void ReconcileBindLocked(string key, BindIntent intent, Slv3DeviceRecord record)
    {
        if (!NeedsBind(intent, record))
        {
            intent.Sends = 0;
            intent.Manual = false;
            if (!_lockBound)
            {
                _intents.Remove(key);
            }
            return;
        }
        // Unlocked, only a manual Bind or a channel-only move of a chain already ours is carried on, never against a live foreign master.
        if (!_lockBound && (IsLiveMasterLocked(record.MasterMac)
            || !(intent.Manual || (IsBoundToUsLocked(record) && record.RxType == intent.TargetRx))))
        {
            _intents.Remove(key);
            return;
        }
        if (!ValidSlot(intent.TargetRx))
        {
            intent.TargetRx = OwnedSlotOrUnusedLocked(key);
        }
        if (!ValidSlot(intent.TargetRx))
        {
            if (!intent.NoSlotLogged)
            {
                intent.NoSlotLogged = true;
                ServiceLog.Warn($"[lianli-wireless] no free slot to bind {key}");
            }
            return;
        }
        intent.NoSlotLogged = false;
        SendBindFrameLocked(record, intent.TargetRx, unbind: false);
        NoteSendLocked(key, intent, "bind", record);
    }

    private bool NeedsBind(BindIntent intent, Slv3DeviceRecord record) =>
        intent.TargetRx != record.RxType || _channel != record.Channel
        || !Slv3Protocol.MacEquals(record.MasterMac, _masterMac);

    // Frames repeat every poll until the chain echoes the target, so the first send and then every SendLogInterval-th are logged.
    private static void NoteSendLocked(string key, BindIntent intent, string what, Slv3DeviceRecord record)
    {
        if (intent.Sends++ % SendLogInterval == 0)
        {
            ServiceLog.Info($"[lianli-wireless] {what} {key} to slot {intent.TargetRx} (send {intent.Sends}), reports master {Convert.ToHexString(record.MasterMac)} slot {record.RxType} ch {record.Channel}");
        }
    }

    // L-Connect RX-slot conflict: a chain reporting a slot that another chain of ours reports too, for more than
    // SlotConflictLimit consecutive polls. Unbinds it as L-Connect does; off while the lock is on.
    private void ResolveSlotConflictsLocked()
    {
        if (_lockBound)
        {
            _conflictCounts.Clear();
            return;
        }
        if (_devicePolls == _conflictPoll)
        {
            return;
        }
        _conflictPoll = _devicePolls;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        (string Key, byte Slot)? loser = null;
        foreach (var record in _pollRecords)
        {
            var key = Convert.ToHexString(record.Mac);
            seenKeys.Add(key);
            if ((_intents.TryGetValue(key, out var intent) && intent.Unbinding) || !ValidSlot(record.RxType)
                || !Slv3Protocol.MacEquals(record.MasterMac, _masterMac)
                || !_knownChains.TryGetValue(key, out var chain) || chain.Poll != _devicePolls
                || !SlotSharedWithLiveChainLocked(record))
            {
                _conflictCounts.Remove(key);
                continue;
            }
            var count = _conflictCounts.GetValueOrDefault(key) + 1;
            _conflictCounts[key] = count;
            if (count > SlotConflictLimit && loser is null)
            {
                loser = (key, record.RxType);
            }
        }
        foreach (var key in new List<string>(_conflictCounts.Keys))
        {
            if (!seenKeys.Contains(key))
            {
                _conflictCounts.Remove(key);
            }
        }
        if (loser is { } lost)
        {
            _conflictCounts.Remove(lost.Key);
            _intents[lost.Key] = new BindIntent(0) { Unbinding = true };
            _unboundHere.Add(lost.Key);
            ServiceLog.Info($"[lianli-wireless] slot {lost.Slot} reported by two chains, unbinding {lost.Key}");
        }
    }

    private bool SlotSharedWithLiveChainLocked(Slv3DeviceRecord record)
    {
        foreach (var other in _lastFanRecords)
        {
            if (Slv3Protocol.MacEquals(other.MasterMac, _masterMac) && other.RxType == record.RxType
                && !Slv3Protocol.MacEquals(other.Mac, record.Mac)
                && !(_intents.TryGetValue(Convert.ToHexString(other.Mac), out var rival) && rival.Unbinding))
            {
                return true;
            }
        }
        return false;
    }

    // L-Connect GetRxUnused: searches upward from the highest slot held, wraps to the lowest, and returns byte.MaxValue when none is free.
    private byte GetRxUnusedLocked()
    {
        byte start = 1;
        foreach (var record in _lastFanRecords)
        {
            if (IsBoundToUsLocked(record) && record.RxType > start)
            {
                start = record.RxType;
            }
        }
        foreach (var key in _intents.Keys)
        {
            if (TryGetRecordByKeyLocked(key, out var record) && Slv3Protocol.MacEquals(record.MasterMac, _masterMac) && record.RxType > start)
            {
                start = record.RxType;
            }
        }
        for (var slot = start; slot <= Slv3Protocol.MaxSlot; slot++)
        {
            if (!RxClaimedLocked((byte)slot, null))
            {
                return (byte)slot;
            }
        }
        for (var slot = Slv3Protocol.MinSlot; slot <= Slv3Protocol.MaxSlot; slot++)
        {
            if (!RxClaimedLocked((byte)slot, null))
            {
                return (byte)slot;
            }
        }
        return byte.MaxValue;
    }

    // A slot another chain is aimed at or a listed chain reports on our master, other than <paramref name="except"/>.
    private bool RxClaimedLocked(byte slot, string? except)
    {
        foreach (var record in _lastFanRecords)
        {
            if (record.RxType == slot && IsBoundToUsLocked(record) && Convert.ToHexString(record.Mac) != except)
            {
                return true;
            }
        }
        return IntentAimedAtLocked(slot, except);
    }

    private bool IntentAimedAtLocked(byte slot, string? except)
    {
        foreach (var (key, intent) in _intents)
        {
            if (key != except && intent.TargetRx == slot)
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetRecordByKeyLocked(string key, out Slv3DeviceRecord record)
    {
        if (_knownChains.TryGetValue(key, out var chain))
        {
            record = chain.Record;
            return true;
        }
        foreach (var r in _pollRecords)
        {
            if (Convert.ToHexString(r.Mac) == key)
            {
                record = r;
                return true;
            }
        }
        record = default;
        return false;
    }

    // Re-send every pending sequenced command, and a SaveCfg for a few seconds after a user Bind.
    // L-Connect does this on every loop pass until the device list echoes the result.
    private void SyncControlLocked()
    {
        List<string>? done = null;
        List<(string Key, Slv3PendingCommand Cmd)>? ticked = null;
        foreach (var (key, cmd) in _pendingCommands)
        {
            var found = TryFindRecordLocked(cmd.Mac, out var record);
            // Theme frames only ever go to a chain that is still ours.
            if ((found && record.CmdSeq == cmd.TargetSeq) || cmd.SendsRemaining <= 0
                || (found && cmd.RfCmd is Slv3Protocol.RfSendColors or Slv3Protocol.RfSwitchWirelessTheme && !IsBoundToUsLocked(record)))
            {
                (done ??= new List<string>()).Add(key);
                continue;
            }
            if (found)
            {
                SendSequencedCommandLocked(record, cmd.RfCmd, cmd.TargetSeq, cmd.Arg, cmd.Data);
            }
            // A chain that dropped out of the list still spends its budget, so a
            // stale command cannot outlive the chain and fire on its return.
            (ticked ??= new List<(string, Slv3PendingCommand)>()).Add((key, cmd with { SendsRemaining = cmd.SendsRemaining - 1 }));
        }
        if (ticked is not null)
        {
            foreach (var (key, cmd) in ticked)
            {
                _pendingCommands[key] = cmd;
            }
        }
        if (done is not null)
        {
            foreach (var key in done)
            {
                _pendingCommands.Remove(key);
            }
        }

        if (_lastBindMs is { } bound && _nowMs() - bound < PostBindSaveCfgMs)
        {
            SendSaveCfgLocked();
        }
    }

    // L-Connect SyncPwm: for every chain bound to us that reports fans, send
    // the bind/PWM frame only when a port's reported duty byte is more than
    // PwmDriftThreshold from its target. The chain echoes the tuple it was
    // last given, so a changed target converges after one frame and a settled
    // chain gets nothing. A chain still being bound or unbound is left to the
    // control pass. L-Connect skips a chain that enumerates no fans; here such
    // a chain is still driven once the user sets a port duty on it (the cooling
    // provider exposes its ports), and left alone on the mobo-sync default.
    // A Strimer has no fan ports at all and is never re-bound from here, nor is
    // a HydroShift II, whose fourth PWM slot drives its pump.
    private void SyncPwmLocked()
    {
        foreach (var record in _lastFanRecords)
        {
            if (!IsBoundToUsLocked(record) || record.IsStrimer || record.IsHydroShift)
            {
                continue;
            }
            var key = Convert.ToHexString(record.Mac);
            if (_intents.TryGetValue(key, out var intent) && (intent.Unbinding || NeedsBind(intent, record)))
            {
                continue;
            }
            var targets = DutyTargetsLocked(record.Mac);
            if (record.FanCount <= 0 && !HasManualTarget(targets))
            {
                continue;
            }
            var pwm = Slv3Protocol.BuildPwmTuple(targets, record.FanCount, record.Family);
            if (!Slv3Protocol.NeedSyncPwm(record.Pwm, pwm))
            {
                _unconfirmedPwm.Remove(key);
                continue;
            }
            var confirmable = _blindSinceMs is null && _knownChains.TryGetValue(key, out var chain) && chain.Poll == _devicePolls;
            if (confirmable)
            {
                _unconfirmedPwm.Remove(key);
            }
            else
            {
                var nowMs = _nowMs();
                if (_unconfirmedPwm.TryGetValue(key, out var sent) && sent.Pwm.AsSpan().SequenceEqual(pwm)
                    && nowMs - sent.SentMs < UnconfirmedPwmResendMs)
                {
                    continue;
                }
                _unconfirmedPwm[key] = (pwm, nowMs);
            }
            SendBindFrameLocked(record, record.RxType, unbind: false);
        }
    }

    // lian-li-linux control_wireless: while Nexus drives an AIO's pump or holds
    // screen settings for it, the switch to RF params is re-sent until the AIO
    // echoes its cmdSeq, and the param block (pump timer plus LCD settings and
    // readings) goes out every DriveTick. The block always carries a pump timer,
    // so a screen-only AIO gets the default pump speed.
    private void SyncAioLocked(Slv3AioSensors sensors, IReadOnlyDictionary<string, Slv3AioScreen>? screens)
    {
        if (_aioControl.Count == 0 && (screens is null || screens.Count == 0))
        {
            return;
        }
        HashSet<string>? bound = null;
        foreach (var record in _lastFanRecords)
        {
            if (!record.IsHydroShift)
            {
                continue;
            }
            var key = Convert.ToHexString(record.Mac);
            if (!IsBoundToUsLocked(record))
            {
                // The AIO's mode after an unbind is unknown, so a rebind sends the switch again.
                if (_aioControl.TryGetValue(key, out var unbound))
                {
                    unbound.Switched = false;
                    unbound.SwitchSeq = 0;
                }
                continue;
            }
            (bound ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
            Slv3AioScreen screen = default;
            var screenSet = screens is not null && screens.TryGetValue(key, out screen);
            if (!_aioControl.TryGetValue(key, out var control))
            {
                if (!screenSet)
                {
                    continue;
                }
                control = new Slv3AioControl();
                _aioControl[key] = control;
                _anyAioControlled = true;
                ServiceLog.Info($"[lianli-wireless] HydroShift II {key} screen held by Nexus");
            }
            else if (control.Percent is null && !screenSet)
            {
                _aioControl.Remove(key);
                _anyAioControlled = _aioControl.Count > 0;
                continue;
            }
            if (!control.Switched)
            {
                if (control.SwitchSeq != 0 && record.CmdSeq == control.SwitchSeq)
                {
                    control.Switched = true;
                    ServiceLog.Info($"[lianli-wireless] HydroShift II {key} acknowledged RF control (seq {control.SwitchSeq})");
                    RaiseAioSwitched(key);
                }
                else if (!_pendingCommands.ContainsKey(key))
                {
                    control.SwitchSeq = QueueSequencedCommandLocked(record, Slv3Protocol.RfAioSwitchWireless) ?? 0;
                }
            }
            var rpm = control.Percent is int percent
                ? Slv3Protocol.HydroShiftPumpRpm(percent, record.DevType)
                : Slv3Protocol.HydroShiftDefaultPumpRpm;
            var shown = screenSet ? screen : Slv3AioScreen.Default;
            // With the AIO's USB cable connected and Nexus widgets on, the streamed panel owns the
            // glass and the panel record's own setting is the only one that applies; this byte
            // stays at the user's value then, the same condition the web uses to hide this toggle.
            var sleeping = _lockSleep is not null && _lockSleep.IsSleeping(key, () => shown.SleepWhenLocked) && !UsbPanelOwnsGlass(key, shown.NexusWidgets);
            shown = shown with { Brightness = AioLockBrightness(key, shown.Brightness, sleeping) };
            var block = Slv3Protocol.BuildAioParamBlock(
                sensors, Slv3Protocol.HydroShiftPumpTimer(rpm, record.DevType),
                shown,
                RadiatorFanRpm(record));
            var payload = Slv3Protocol.BuildAioParams(record.Mac, _masterMac, record.RxType, record.Channel, BindOrdinalLocked(record.Mac), block);
            SendRfPayloadLocked(record.Channel, record.RxType, payload);
        }
        // A screen that left (unbound, unplugged, expired) forgets its lock state, so the next sight of
        // it counts as a first sight and goes straight to the state, not along a stale ramp.
        if (_aioLockSlept.Count > 0)
        {
            foreach (var gone in _aioLockSlept.Keys.Where(k => bound is null || !bound.Contains(k)).ToList())
            {
                _aioLockSlept.Remove(gone);
                _aioFades.Remove(gone);
            }
        }
        // A released pump on an AIO that is no longer bound here has nothing left to hold.
        List<string>? released = null;
        foreach (var (key, control) in _aioControl)
        {
            if (control.Percent is null && (bound is null || !bound.Contains(key)))
            {
                (released ??= new List<string>()).Add(key);
            }
        }
        if (released is not null)
        {
            foreach (var key in released)
            {
                _aioControl.Remove(key);
            }
            _anyAioControlled = _aioControl.Count > 0;
        }
    }

    // The lock fade for the screen's brightness byte. It rides the param block that already goes out
    // every drive tick, so the ramp has exactly as many steps as ticks land inside it and adds no
    // send. Guarded by _lock, like the rest of SyncAioLocked.
    private readonly Dictionary<string, bool> _aioLockSlept = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LcdFade> _aioFades = new(StringComparer.Ordinal);

    private byte AioLockBrightness(string key, byte user, bool sleeping)
    {
        var now = _nowMs();
        if (!_aioLockSlept.TryGetValue(key, out var was))
        {
            // First sight of this screen (it came up mid-lock): go straight to the state, no ramp.
            _aioLockSlept[key] = sleeping;
            return sleeping ? (byte)0 : user;
        }
        if (sleeping != was)
        {
            _aioLockSlept[key] = sleeping;
            // From the level the screen shows now, or the step a ramp in flight is on, to the other
            // end. The level it comes back to is the user's setting, which nothing here changes.
            var from = _aioFades.TryGetValue(key, out var running) ? running.LevelAt(now) : was ? 0 : user;
            var to = sleeping ? 0 : user;
            if (from == to)
            {
                _aioFades.Remove(key);
            }
            else
            {
                _aioFades[key] = new LcdFade(from, to, sleeping ? SleepBlackoutCoordinator.LockFadeDuration : SleepBlackoutCoordinator.UnlockFadeDuration, now);
            }
        }
        if (_aioFades.TryGetValue(key, out var fade))
        {
            var level = fade.LevelAt(now);
            if (fade.IsDoneAt(now))
            {
                _aioFades.Remove(key);
            }
            return (byte)level;
        }
        return sleeping ? (byte)0 : user;
    }

    private bool UsbPanelOwnsGlass(string key, bool nexusWidgets) =>
        nexusWidgets && string.Equals(_usbAioMac, key, StringComparison.OrdinalIgnoreCase);

    private void RefreshLockSetting()
    {
        if (_lockRead is not { } read)
        {
            return;
        }
        try
        {
            var version = Volatile.Read(ref _lockVersion);
            var on = read();
            lock (_lockSettingGate)
            {
                if (version == _lockVersion)
                {
                    _lockBound = on;
                    State.LockBoundDevices = on;
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] lock setting read failed: {ex.Message}");
        }
    }

    private string? ReadUsbAioMac()
    {
        try
        {
            return UsbAioMac?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] USB AIO lookup failed: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyDictionary<string, Slv3AioScreen>? ReadAioScreens()
    {
        try
        {
            return AioScreens?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] AIO screen settings read failed: {ex.Message}");
            return null;
        }
    }

    // Average of the HydroShift II's spinning fan ports; its pump has its own slot.
    private static int RadiatorFanRpm(Slv3DeviceRecord record)
    {
        var sum = 0;
        var spinning = 0;
        for (var port = 0; port < Slv3Protocol.HydroShiftPumpPort && port < record.Rpm.Length; port++)
        {
            if (record.Rpm[port] <= 0) continue;
            sum += record.Rpm[port];
            spinning++;
        }
        return spinning == 0 ? 0 : sum / spinning;
    }

    private Slv3AioSensors ReadAioSensors()
    {
        try
        {
            return AioSensors?.Invoke() ?? default;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] AIO sensor read failed: {ex.Message}");
            return default;
        }
    }

    private static bool HasManualTarget(int?[] targets)
    {
        foreach (var target in targets)
        {
            if (target is not null)
            {
                return true;
            }
        }
        return false;
    }

    // L-Connect QuerryMasterMac once a second: refreshes the master identity,
    // RF timer and TX firmware version. A missed reply keeps the last known
    // values and the link; a run of silent probes closes the TX for the reopen
    // path. Any reply proves the TX alive, even without a master MAC.
    private void RefreshMasterMacLocked()
    {
        if (_tx is not { IsOpen: true })
        {
            return;
        }
        TryGetMacOnChannelLocked(_channel, out var probe);
        if (probe == TxProbe.Answered)
        {
            _txMissedProbes = 0;
            _txProbeWriteFailures = 0;
            return;
        }
        if (probe == TxProbe.WriteFailed)
        {
            _txProbeWriteFailures++;
        }
        if (++_txMissedProbes < TxMissedProbesBeforeReopen)
        {
            return;
        }
        ServiceLog.Warn($"[lianli-wireless] TX missed {_txMissedProbes} GetMac probes in a row ({_txProbeWriteFailures} failed writes), reopening it");
        _txMissedProbes = 0;
        _txProbeWriteFailures = 0;
        try { _tx.Dispose(); } catch { /* best effort */ }
        _tx = null;
    }

    private void NoteConfigChangedLocked()
    {
        var now = _nowMs();
        _lastConfigChangeMs = now;
        if (_saveCfgDueMs == 0)
        {
            _saveCfgDueMs = now + SaveCfgDelayMs;
        }
    }

    // L-Connect CheckSaveConfig plus the debounced save. A closed TX defers a due save until it reopens.
    private void RunSaveCfgScheduleLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var now = _nowMs();
        if (_periodicBaseMs == 0)
        {
            _periodicBaseMs = now;
            _lastSaveMs = now;
        }
        var periodic = (!_firstPeriodicSaveDone && now - _periodicBaseMs > PeriodicFirstSaveMs) || now - _lastSaveMs > PeriodicSaveMs;
        if (periodic && now - _lastConfigChangeMs > PeriodicEffectQuietMs)
        {
            _firstPeriodicSaveDone = true;
            _saveCfgDueMs = 0;
            SendSaveCfgLocked();
            return;
        }
        if (_saveCfgDueMs == 0 || now < _saveCfgDueMs)
        {
            return;
        }
        if (now - _lastConfigChangeMs < SaveCfgQuietMs)
        {
            _saveCfgDueMs = now + SaveCfgDelayMs;
            return;
        }
        _saveCfgDueMs = 0;
        SendSaveCfgLocked();
    }

    private bool SendSaveCfgLocked()
    {
        if (_tx is null)
        {
            return false;
        }
        _lastSaveMs = _nowMs();
        return WriteRfPayloadLocked(_channel, 0xFF, Slv3Protocol.BuildSaveCfg(_masterMac));
    }

    // Pages needed to hold recordCount records at RecordsPerPage each, >=1 and
    // capped at MaxDeviceListPages so a corrupt count can't request a huge read.
    private static byte DeviceListPagesFor(int recordCount) =>
        (byte)Math.Clamp(
            (recordCount + Slv3Protocol.RecordsPerPage - 1) / Slv3Protocol.RecordsPerPage,
            1, MaxDeviceListPages);

    private bool RefreshDeviceListLocked()
    {
        if (_rx is null)
        {
            return false;
        }
        // <=10 records/page - request ceil(count/10) pages so a device list that
        // spans more than one page (up to MaxSlot chains plus non-fan devices)
        // isn't truncated to the first 10. Dropped records vanish from the list,
        // so those chains can't be seen, paired, or confirm a bind. Seed pageCount
        // from the last poll's reported count (the reference auto-tunes the same
        // way); a chain that just appeared is picked up on the next poll.
        var pageCount = DeviceListPagesFor(_lastRecordCount);
        if (!_rx.RfSend(Slv3Protocol.BuildGetDev(pageCount)))
        {
            // A failed write counts toward the same streak as an unreadable reply.
            return HandleGetDevFailureLocked();
        }
        var reply = _rx.RfRead(Slv3Protocol.PageLength * pageCount);
        // A missing/invalid GetDev echo with a healthy handle is a wedged RX
        // MCU (streak -> 0x15 reset); a valid reply listing zero devices is an
        // RF sampling gap and takes the normal merge path.
        if (reply.Length > 0 && reply[0] == 0)
        {
            return HandleRxBusyLocked(reply);
        }
        if (reply.Length < Slv3Protocol.RecordHeaderLength || reply[0] != Slv3Protocol.UsbSendRf)
        {
            return HandleGetDevFailureLocked();
        }
        _rxFailStreak = 0;
        _rxBusyStreak = 0;
        _rxRecoveries = 0;
        _nextRxRecoveryMs = 0;
        if (State.LinkStatus == Slv3LinkStatus.Recovering && _tx is { IsOpen: true })
        {
            State.LinkStatus = Slv3LinkStatus.Ok;
        }

        State.MotherboardPwmPercent = Slv3Protocol.ParseGetDevMoboDuty(reply);

        var count = Slv3Protocol.RecordCount(reply);
        var nowMs = _nowMs();
        if (_blindSinceMs is { } blindSince)
        {
            _blindTotalMs += nowMs - blindSince;
            _blindSinceMs = null;
        }
        _devicePolls++;
        var pollRecords = new List<Slv3DeviceRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
            var parsed = Slv3Protocol.TryParseRecord(reply, offset, out var record);
            if (parsed && record.IsMaster)
            {
                _liveMasters[Convert.ToHexString(record.Mac)] = _devicePolls;
            }
            else if (parsed && record.IsWirelessFan)
            {
                pollRecords.Add(record);
                var key = Convert.ToHexString(record.Mac);
                if (!_knownChains.TryGetValue(key, out var known))
                {
                    if (IsZeroedCopyOfKnownChainLocked(record.Mac))
                    {
                        continue;
                    }
                    if (ConfirmNewChains)
                    {
                        var seen = _unconfirmedChains.TryGetValue(key, out var u)
                            ? (u.LastPoll == _devicePolls ? u : u with { LastPoll = _devicePolls, Sightings = u.Sightings + 1 })
                            : new Slv3Unconfirmed(_devicePolls, _devicePolls, 1);
                        _unconfirmedChains[key] = seen;
                        if (seen.Sightings < ChainConfirmSightings)
                        {
                            continue;
                        }
                    }
                    _unconfirmedChains.Remove(key);
                    var what = record.IsStrimer ? $"Strimer dev_type {record.DevType}"
                        : record.IsHydroShift ? $"HydroShift II dev_type {record.DevType}, {record.FanCount} fan(s)"
                        : $"{record.FanCount} fan(s), {record.Family}";
                    ServiceLog.Info($"[lianli-wireless] chain {key} appeared ({what}, {DescribeBinding(record, _masterMac)})");
                }
                else if (TraceBindingLocked(key, known.Record, record, reply, offset, nowMs) is { } line)
                {
                    ServiceLog.Info(line);
                }
                _knownChains[key] = new Slv3KnownChain(record, nowMs, _devicePolls, _blindTotalMs);
            }
        }
        _pollRecords = pollRecords;
        UpdateReportOrderLocked(pollRecords);

        ExpireChainsLocked(nowMs);

        // Page estimate follows the larger of the reply's count and the tracked
        // set, so a partial report can't shrink the next read below the full list.
        _lastRecordCount = Math.Max(count, _knownChains.Count);
        PublishFansLocked(nowMs);
        return true;
    }

    // The confirmed chains of this reply in report order, then chains an incomplete reply left out in their old order, then any known chain still missing.
    private void UpdateReportOrderLocked(List<Slv3DeviceRecord> pollRecords)
    {
        var order = new List<string>(_knownChains.Count);
        foreach (var record in pollRecords)
        {
            var key = Convert.ToHexString(record.Mac);
            if (_knownChains.ContainsKey(key) && !order.Contains(key))
            {
                order.Add(key);
            }
        }
        foreach (var key in _reportOrder)
        {
            if (_knownChains.ContainsKey(key) && !order.Contains(key))
            {
                order.Add(key);
            }
        }
        foreach (var key in _knownChains.Keys)
        {
            if (!order.Contains(key))
            {
                order.Add(key);
            }
        }
        _reportOrder = order;
    }

    private void ExpireChainsLocked(long nowMs)
    {
        var blindMs = _blindTotalMs + (_blindSinceMs is { } since ? nowMs - since : 0);
        List<string>? gone = null;
        foreach (var (master, poll) in new List<KeyValuePair<string, long>>(_liveMasters))
        {
            if (_devicePolls - poll >= LiveMasterPolls)
            {
                _liveMasters.Remove(master);
            }
        }
        foreach (var (key, unconfirmed) in _unconfirmedChains)
        {
            if (_devicePolls - unconfirmed.FirstPoll >= ChainConfirmWindowPolls)
            {
                (gone ??= new List<string>()).Add(key);
            }
        }
        if (gone is not null)
        {
            foreach (var key in gone) _unconfirmedChains.Remove(key);
            gone = null;
        }
        foreach (var (key, chain) in _knownChains)
        {
            if (nowMs - chain.LastSeenMs - (blindMs - chain.BlindMsAtSeen) > ChainExpiryMs)
            {
                (gone ??= new List<string>()).Add(key);
            }
        }
        if (gone is not null)
        {
            foreach (var key in gone)
            {
                ServiceLog.Info($"[lianli-wireless] chain {key} dropped ({ChainExpiryMs / 1000}s unseen)");
                _knownChains.Remove(key);
                _intents.Remove(key);
                _unboundHere.Remove(key);
                _conflictCounts.Remove(key);
                _ownedSlotSightings.Remove(key);
                _bindingLogs.Remove(key);
                _unconfirmedPwm.Remove(key);
                if (_aioControl.TryGetValue(key, out var control))
                {
                    control.Switched = false;
                    control.SwitchSeq = 0;
                }
            }
        }
    }

    // A poll that got no device list from the RX.
    private void AgeChainsLocked(long nowMs)
    {
        _blindSinceMs ??= nowMs;
        _pollRecords = new List<Slv3DeviceRecord>();
        ExpireChainsLocked(nowMs);
        PublishFansLocked(nowMs);
    }

    // Surfaces the known chains, flagging any unseen for longer than
    // ChainStaleMs, including while the RX reports no list.
    private void PublishFansLocked(long nowMs)
    {
        var records = new List<Slv3DeviceRecord>(_knownChains.Count);
        var fans = new List<Slv3FanInfo>(_knownChains.Count);
        foreach (var key in SortedChainKeysLocked())
        {
            var chain = _knownChains[key];
            records.Add(chain.Record);
            fans.Add(ToFanInfo(chain.Record, stale: nowMs - chain.LastSeenMs > ChainStaleMs));
        }
        _lastFanRecords = records;
        _anyAioBound = records.Exists(r => r.IsHydroShift && IsBoundToUsLocked(r));
        State.Fans = fans.ToArray();
    }

    // A corrupt RX copy of a real chain: same MAC with some bytes zeroed.
    private bool IsZeroedCopyOfKnownChainLocked(byte[] mac)
    {
        foreach (var chain in _knownChains.Values)
        {
            var known = chain.Record.Mac;
            if (known.Length != mac.Length || Slv3Protocol.MacEquals(known, mac))
            {
                continue;
            }
            var copy = true;
            for (var i = 0; i < mac.Length && copy; i++)
            {
                copy = mac[i] == known[i] || mac[i] == 0;
            }
            if (copy)
            {
                return true;
            }
        }
        return false;
    }

    // MAC-ordered keys so the surfaced list is stable across polls regardless
    // of dictionary iteration order.
    private List<string> SortedChainKeysLocked()
    {
        var keys = new List<string>(_knownChains.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    // The RX answered but has no list: keep the last-known one.
    private bool HandleRxBusyLocked(byte[] reply)
    {
        AgeChainsLocked(_nowMs());
        if (++_rxBusyStreak == 1)
        {
            _firstBusyReply = reply;
        }
        if (_rxBusyStreak < RxBusyPollsBeforeReset)
        {
            return true;
        }
        var first = _firstBusyReply is { } b ? Convert.ToHexString(b, 0, Math.Min(b.Length, Slv3Protocol.UsbPacketSize)) : "";
        return BeginRxRecoveryLocked($"{RxBusyPollsBeforeReset} consecutive busy GetDev replies (first {first})", resetViaTx: false);
    }

    // Only a good reply clears the busy and failure streaks, so an RX that
    // alternates between the two still escalates.
    private bool HandleGetDevFailureLocked()
    {
        AgeChainsLocked(_nowMs());
        _rxFailStreak++;
        if (_rxFailStreak < RxFailStreakForReset)
        {
            // Transient: keep the last-known list and let the next tick retry.
            return true;
        }
        return BeginRxRecoveryLocked($"{RxFailStreakForReset} consecutive GetDev failures", resetViaTx: true);
    }

    // Resets the RX half by writing UsbResetAnother on the TX handle, or just
    // reopens the RX handle when asked to or when the TX is unavailable. The
    // reopen retries each tick until the RX enumerates. Never ends the link.
    private bool BeginRxRecoveryLocked(string reason, bool resetViaTx)
    {
        var nowMs = _nowMs();
        if (nowMs < _nextRxRecoveryMs)
        {
            return true;
        }
        _rxFailStreak = 0;
        _rxBusyStreak = 0;
        _rxRecoveries++;
        _nextRxRecoveryMs = nowMs + RecoveryBackoffFor(_rxRecoveries);
        State.LinkStatus = Slv3LinkStatus.Recovering;
        var reset = resetViaTx && _tx is { IsOpen: true } && _tx.RfSend(Slv3Protocol.BuildResetAnother());
        ServiceLog.Warn($"[lianli-wireless] {reason}, {(reset ? "resetting RX via TX" : "reopening RX")} ({_rxRecoveries})");
        try { _rx?.Dispose(); } catch { /* best effort */ }
        _rx = null;
        if (!reset)
        {
            ReopenRxLocked(FindPortLocked(Slv3DongleRole.Rx));
        }
        return true;
    }

    private Slv3PortInfo? FindPortLocked(Slv3DongleRole role)
    {
        foreach (var port in _discovery.Discover())
        {
            if (port.Role == role)
            {
                return port;
            }
        }
        return null;
    }

    // A half that is not enumerated is reset again on the recovery backoff; one that
    // enumerates but fails to open is retried on the same backoff, logging once per step.
    private void ReopenRxLocked(Slv3PortInfo? rxPort)
    {
        var nowMs = _nowMs();
        try { _rx?.Dispose(); } catch { /* best effort */ }
        _rx = null;
        if (rxPort is null)
        {
            if (_tx is { IsOpen: true } && nowMs >= _nextRxRecoveryMs)
            {
                _rxRecoveries++;
                _nextRxRecoveryMs = nowMs + RecoveryBackoffFor(_rxRecoveries);
                if (_tx.RfSend(Slv3Protocol.BuildResetAnother()))
                {
                    ServiceLog.Warn($"[lianli-wireless] RX not enumerated, resetting RX via TX ({_rxRecoveries})");
                }
            }
            return;
        }
        if (nowMs < _nextRxOpenMs)
        {
            return;
        }
        try
        {
            _rx = _transportFactory(rxPort);
        }
        catch (Exception ex)
        {
            _rxOpenFailures++;
            _nextRxOpenMs = nowMs + RecoveryBackoffFor(_rxOpenFailures);
            ServiceLog.Warn($"[lianli-wireless] RX reopen failed ({_rxOpenFailures}): {ex.GetType().Name}: {ex.Message}");
            return;
        }
        _rxOpenFailures = 0;
        _nextRxOpenMs = 0;
        ServiceLog.Info("[lianli-wireless] RX reopened");
    }

    // Bound to us = our master MAC and a valid slot. A release clears both in
    // the chain's record; a stale master with slot 0 is unbound.
    private bool IsBoundToUsLocked(Slv3DeviceRecord record) => IsBoundTo(record, _masterMac);

    private static bool IsBoundTo(Slv3DeviceRecord record, byte[] masterMac) =>
        Slv3Protocol.MacEquals(record.MasterMac, masterMac)
        && Slv3Protocol.IsBoundSlot(record.RxType);

    // A flapping record logs at most once per BindingLogIntervalMs per chain, so
    // it cannot push older lines out of the support bundle's log tail.
    internal string? TraceBindingLocked(string key, Slv3DeviceRecord before, Slv3DeviceRecord after, byte[] reply, int offset, long nowMs)
    {
        var changed = BindingChanged(before, after);
        var found = _bindingLogs.TryGetValue(key, out var log);
        var quiet = found && nowMs - log.LastMs < BindingLogIntervalMs;
        if (changed && quiet)
        {
            _bindingLogs[key] = log with { Unlogged = log.Unlogged + 1 };
            return null;
        }
        var unlogged = log.Unlogged > 0 ? $" after {log.Unlogged} unlogged change(s)" : "";
        string line;
        if (changed)
        {
            line = $"[lianli-wireless] chain {key} binding {DescribeBinding(before, _masterMac)} -> {DescribeBinding(after, _masterMac)}{unlogged}, record {Convert.ToHexString(reply, offset, Slv3Protocol.RecordLength)}";
        }
        else if (log.Unlogged > 0 && !quiet)
        {
            line = $"[lianli-wireless] chain {key} binding now at {DescribeBinding(after, _masterMac)}{unlogged}";
        }
        else
        {
            return null;
        }
        _bindingLogs[key] = new Slv3BindingLog(nowMs, 0);
        return line;
    }

    internal static bool BindingChanged(Slv3DeviceRecord before, Slv3DeviceRecord after) =>
        !Slv3Protocol.MacEquals(before.MasterMac, after.MasterMac)
        || before.RxType != after.RxType
        || before.Channel != after.Channel;

    internal static string DescribeBinding(Slv3DeviceRecord record, byte[] masterMac) =>
        $"master {Convert.ToHexString(record.MasterMac)} slot {record.RxType} ch {record.Channel} {(IsBoundTo(record, masterMac) ? "bound" : "unbound")}";

    private Slv3FanInfo ToFanInfo(Slv3DeviceRecord record, bool stale) => new()
    {
        Mac = Convert.ToHexString(record.Mac),
        MasterMac = Slv3Protocol.MacIsZero(record.MasterMac) ? "" : Convert.ToHexString(record.MasterMac),
        BoundToUs = IsBoundToUsLocked(record),
        Channel = record.Channel,
        Slot = record.RxType,
        DevType = record.DevType,
        FanType = record.EffectiveFanType,
        FanCount = record.FanCount,
        Rpm = (int[])record.Rpm.Clone(),
        Pwm = (int[])record.Pwm.Clone(),
        EffectIndex = Convert.ToHexString(record.EffectIndex),
        Stale = stale,
        UsbConnected = (record.IsHydroShift && _usbAioMac is { } usbMac
            && string.Equals(usbMac, Convert.ToHexString(record.Mac), StringComparison.OrdinalIgnoreCase))
            || (IsWiredOwned(Convert.ToHexString(record.Mac)) && !IsBoundToUsLocked(record)),
        WiredOwned = IsWiredOwned(Convert.ToHexString(record.Mac)) && !IsBoundToUsLocked(record),
        RadioDrivesUsb = IsWiredOwned(Convert.ToHexString(record.Mac)) && IsBoundToUsLocked(record),
        CoolantTempC = record.CoolantTempC,
        FirmwareVersion = record.RfVersion,
        ArgbCableConnected = record.ArgbCableConnected,
        PlayingMotherboardArgb = record.PlayingMotherboardArgb,
        PwmCableConnected = record.PwmCableConnected,
    };

    private bool TryFindRecordLocked(byte[] mac, out Slv3DeviceRecord record)
    {
        foreach (var r in _lastFanRecords)
        {
            if (Slv3Protocol.MacEquals(r.Mac, mac))
            {
                record = r;
                return true;
            }
        }
        record = default;
        return false;
    }

    // L-Connect writes the chain's 1-based position among the chains bound to
    // this master (SyncControlInfo's running counter) at bind-frame byte [16],
    // not its rx_type slot. Equal for a single chain; they diverge once a slot
    // is freed in the middle.
    private byte BindOrdinalLocked(byte[] mac)
    {
        var ordinal = 0;
        foreach (var key in SortedChainKeysLocked())
        {
            var record = _knownChains[key].Record;
            var bound = IsBoundToUsLocked(record)
                || (_intents.TryGetValue(key, out var intent) && !intent.Unbinding);
            if (!bound)
            {
                continue;
            }
            ordinal++;
            if (Slv3Protocol.MacEquals(record.Mac, mac))
            {
                return (byte)ordinal;
            }
        }
        return (byte)Math.Max(1, ordinal + 1);
    }

    // Addressed at the chain's CURRENT (channel, rx) exactly as its record reports it, garbled or not,
    // as L-Connect does; the payload's target fields carry what to reconfigure to. Caller holds _lock.
    private bool SendBindFrameLocked(Slv3DeviceRecord record, byte targetSlot, bool unbind)
    {
        if (_tx is null)
        {
            return false;
        }
        // A duty set on a HydroShift II through the cooling route would land in its pump slot.
        var targets = record.IsHydroShift ? DefaultDutyTargets : DutyTargetsLocked(record.Mac);
        var pwm = Slv3Protocol.BuildPwmTuple(targets, record.FanCount, record.Family);
        var payload = unbind
            ? Slv3Protocol.BuildUnbind(record.Mac, _channel, pwm)
            : Slv3Protocol.BuildBind(record.Mac, _masterMac, targetRx: targetSlot, targetChannel: _channel, slot: BindOrdinalLocked(record.Mac), pwm);
        return SendRfPayloadLocked(record.Channel, record.RxType, payload);
    }

    private bool SendSequencedCommandLocked(Slv3DeviceRecord record, byte rfCmd, byte cmdSeq, byte arg = 0, byte[]? data = null)
    {
        if (_tx is null)
        {
            return false;
        }
        // lian-li-linux switch_to_wireless_theme carries the AIO's bind ordinal at [16].
        var slot = rfCmd == Slv3Protocol.RfAioSwitchWireless
            ? BindOrdinalLocked(record.Mac)
            : rfCmd is Slv3Protocol.RfSendColors or Slv3Protocol.RfSwitchWirelessTheme ? FlexLcdOrdinalLocked(record) : (byte)0;
        var payload = Slv3Protocol.BuildSequencedCommand(rfCmd, record.Mac, _masterMac, record.RxType, record.Channel, cmdSeq, slot, arg, data);
        return SendRfPayloadLocked(record.Channel, record.RxType, payload);
    }

    // Caller holds _lock.
    private int?[] DutyTargetsLocked(byte[] mac)
    {
        var key = Convert.ToHexString(mac);
        return _dutyTargets.TryGetValue(key, out var targets) ? targets : DefaultDutyTargets;
    }

    // L-Connect SyncMasterClock: once a second, broadcast pipe (USB rx 0xFF).
    private void SendClockHeartbeatLocked(byte[]? lcdBlock = null)
    {
        if (_tx is null)
        {
            return;
        }
        WriteRfPayloadLocked(_channel, 0xFF, Slv3Protocol.BuildClockSync(_masterMac, DateTime.Now, lcdBlock));
    }

    /// <summary>
    /// Binds a discovered fan to our master into L-Connect's next unused slot and keeps
    /// reconciling it from the poll ticks. Refused, with nothing changed and the reason in
    /// <paramref name="error"/>, for an unknown MAC, a fan a live neighbor master holds, a
    /// full device cap (devices, Strimers, or HydroShift IIs of one model, per the Bind cap constants) or no free slot.
    /// Also refused with <see cref="LockedError"/> while the lock is on.
    /// A fan already bound to us keeps its slot.
    /// </summary>
    public bool Bind(string macHex, out string error)
    {
        error = "";
        if (IsWiredOwned(macHex))
        {
            error = WiredOwnedError;
            return false;
        }
        if (_lockBound)
        {
            error = LockedError;
            return false;
        }
        if (!TryParseMac(macHex, out var mac))
        {
            error = "invalid mac";
            return false;
        }
        lock (_lock)
        {
            if (_lockBound)
            {
                error = LockedError;
                return false;
            }
            if (!TryFindRecordLocked(mac, out var existing))
            {
                error = "not in the device list";
                return false;
            }
            var key = Convert.ToHexString(mac);
            var alreadyBound = IsBoundToUsLocked(existing);
            if (!alreadyBound)
            {
                if (IsLiveMasterLocked(existing.MasterMac))
                {
                    error = "held by a live master";
                    return false;
                }
                if (BindCapReachedLocked(key, existing) is { } capError)
                {
                    error = capError;
                    return false;
                }
            }
            var slot = alreadyBound ? existing.RxType : GetRxUnusedLocked();
            if (!ValidSlot(slot))
            {
                error = "no free slot";
                return false;
            }
            _userUnbound.Remove(key);
            _unboundHere.Remove(key);
            _deferredUnbinds.Remove(key);
            _intents[key] = new BindIntent(slot) { Manual = true };
            if (alreadyBound)
            {
                return true;
            }
            ServiceLog.Info($"[lianli-wireless] bind requested for {key} on slot {slot}");
            _owned[key] = slot;
            _ownedDirty = true;
            _lastBindMs = _nowMs();
            SendBindFrameLocked(existing, slot, unbind: false);
        }
        FlushOwned();
        return true;
    }

    // L-Connect RFController.Bind caps, counted over devices bound to us or being bound.
    private string? BindCapReachedLocked(string key, Slv3DeviceRecord target)
    {
        int devices = 0, strimers = 0, sameHydroShift = 0;
        foreach (var record in _lastFanRecords)
        {
            var recordKey = Convert.ToHexString(record.Mac);
            if (recordKey == key
                || !(IsBoundToUsLocked(record) || (_intents.TryGetValue(recordKey, out var intent) && !intent.Unbinding)))
            {
                continue;
            }
            devices++;
            strimers += record.IsStrimer ? 1 : 0;
            sameHydroShift += record.IsHydroShift && record.DevType == target.DevType ? 1 : 0;
        }
        if (devices >= BindCapDevices)
        {
            return $"limit of {BindCapDevices} bound devices reached";
        }
        if (target.IsStrimer && strimers >= BindCapStrimers)
        {
            return $"limit of {BindCapStrimers} bound Strimers reached";
        }
        return target.IsHydroShift && sameHydroShift >= BindCapHydroShiftPerType
            ? $"limit of {BindCapHydroShiftPerType} bound HydroShift II of this model reached"
            : null;
    }

    /// <summary>
    /// Releases a fan: a zero-master frame goes to the pipe it reports on every poll until its record
    /// shows no master, whoever holds it now. Refused for an unknown MAC or a fan a live neighbor master
    /// holds. Refused with <see cref="LockedError"/> while the lock is on. Otherwise the fan is dropped from the owned list and never retaken.
    /// </summary>
    public bool Unbind(string macHex, out string error)
    {
        error = "";
        if (_lockBound)
        {
            error = LockedError;
            return false;
        }
        if (!TryParseMac(macHex, out var mac))
        {
            error = "invalid mac";
            return false;
        }
        lock (_lock)
        {
            if (_lockBound)
            {
                error = LockedError;
                return false;
            }
            if (!TryFindRecordLocked(mac, out var existing))
            {
                error = "not in the device list";
                return false;
            }
            var key = Convert.ToHexString(mac);
            // A fan the USB path drives is only ever released here, so the USB path can take it over.
            if (IsWiredOwned(macHex) && !IsBoundToUsLocked(existing))
            {
                error = WiredOwnedError;
                return false;
            }
            if (!IsBoundToUsLocked(existing) && IsLiveMasterLocked(existing.MasterMac))
            {
                error = "held by a live master";
                return false;
            }
            if (_deferredUnbinds.ContainsKey(key))
            {
                return true;
            }
            if (IsBoundToUsLocked(existing) && BeginFlexLcdReleaseForUnbindLocked(existing))
            {
                // The screens go back to USB content first; the release frame follows their acknowledgement or the hold.
                _deferredUnbinds[key] = new DeferredUnbind(_nowMs() + FlexLcdUnbindHoldMs, macHex);
                ServiceLog.Info($"[lianli-wireless] unbind of {key} waits for its wireless theme switch to clear");
                return true;
            }
            PerformUnbindLocked(key, macHex, existing);
        }
        FlushOwned();
        return true;
    }

    private void PerformUnbindLocked(string key, string macHex, Slv3DeviceRecord? existing)
    {
        _userUnbound.Add(key);
        _unboundHere.Add(key);
        if (_owned.Remove(key))
        {
            _ownedDirty = true;
        }
        if (existing is not { } target || Slv3Protocol.MacIsZero(target.MasterMac))
        {
            _intents.Remove(key);
        }
        else
        {
            ServiceLog.Info($"[lianli-wireless] unbind requested for {key}");
            _intents[key] = new BindIntent(0) { Unbinding = true, Sends = IsWiredOwned(macHex) ? 1 : 0 };
            SendBindFrameLocked(target, 0, unbind: true);
        }
    }

    /// <summary>
    /// Arms the TX for wireless-LCD frame traffic: CMD_VIDEO_START then one
    /// prep frame per known device (lian-li-linux ensure_video_mode). L-Connect
    /// sends this before streaming to the SL-LCD screens; the screens' video RF
    /// shares the 2.4 GHz air with this control link (Y70: fan telemetry
    /// collapsed once 3 screens started streaming without it). Idempotent until
    /// the next disconnect.
    /// </summary>
    public bool EnsureVideoMode()
    {
        lock (_lock)
        {
            // Re-arm when chains appeared after the last arming: the LCD loops
            // call this on USB attach, typically before the first GetDev poll
            // has populated the chain list, and un-prepped chains would keep
            // colliding with the video link.
            var deviceCount = Math.Max(1, _knownChains.Count);
            if (_videoModeActive && deviceCount <= _videoModePreppedCount)
            {
                return true;
            }
            if (_tx is null)
            {
                return false;
            }
            if (!_tx.RfSend(Slv3Protocol.BuildVideoStart()))
            {
                return false;
            }
            // Video-start is wire-identical to GetMac(channel 1); drain the
            // reply so it cannot be consumed by a later channel scan.
            _tx.RfRead(Slv3Protocol.UsbPacketSize);
            // Reference gaps: 2 ms after video-start, 1 ms between prep frames.
            Thread.Sleep(2);
            for (var i = 0; i < deviceCount; i++)
            {
                if (!_tx.RfSend(Slv3Protocol.BuildVideoPrep((byte)i, _channel)))
                {
                    return false;
                }
                Thread.Sleep(1);
            }
            _videoModeActive = true;
            _videoModePreppedCount = deviceCount;
            ServiceLog.Info($"[lianli-wireless] video mode armed ({deviceCount} device(s))");
            return true;
        }
    }

    /// <summary>
    /// Sends RF RebootLcd (0x16) at a chain: the first frame now, then once per
    /// poll until the chain's record echoes the command sequence (L-Connect
    /// RebootLcdGroup, up to <see cref="SequencedCommandBudget"/> sends).
    /// Recovery attempt for a chain that beacons header-only records (0 fans,
    /// no RPM) while staying reachable.
    /// </summary>
    public bool ResetChain(string macHex) => QueueSequencedCommand(macHex, Slv3Protocol.RfRebootChain, "chain reset");

    /// <summary>
    /// Flashes a fan for identification: RF_Select now, then once per poll
    /// until the chain echoes the command sequence (L-Connect SelectedGroup).
    /// </summary>
    public bool Identify(string macHex) => QueueSequencedCommand(macHex, Slv3Protocol.RfSelect, "identify");

    /// <summary>True while a sequenced command other than <paramref name="rfCmd"/> waits for the chain's echo; a chain holds one at a time.</summary>
    public bool HasOtherPendingCommand(string macHex, byte rfCmd)
    {
        lock (_lock)
        {
            return _pendingCommands.TryGetValue(macHex, out var cmd) && cmd.RfCmd != rfCmd;
        }
    }

    /// <summary>Hands a chain to its motherboard ARGB input (on) or back to the host (off), re-sent until the chain echoes the command sequence.</summary>
    public bool SetMotherboardArgb(string macHex, bool on) =>
        QueueSequencedCommand(macHex, Slv3Protocol.RfArgbSyncSwitch, on ? "motherboard ARGB on" : "motherboard ARGB off", on ? (byte)1 : (byte)0);

    private bool QueueSequencedCommand(string macHex, byte rfCmd, string label, byte arg = 0)
    {
        if (IsWiredOnly(macHex))
        {
            return false;
        }
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record))
            {
                return false;
            }
            if (QueueSequencedCommandLocked(record, rfCmd, arg) is not { } seq)
            {
                return false;
            }
            ServiceLog.Info($"[lianli-wireless] {label} sent to {macHex} (seq {seq})");
            return true;
        }
    }

    // Sends the first frame now and leaves the rest to SyncControlLocked; null when the send failed.
    private byte? QueueSequencedCommandLocked(Slv3DeviceRecord record, byte rfCmd, byte arg = 0, byte[]? data = null)
    {
        var key = Convert.ToHexString(record.Mac);
        _lastIssuedSeq.TryGetValue(key, out var lastIssued);
        var seq = Slv3Protocol.NextCmdSeq((byte)Math.Max(lastIssued, record.CmdSeq));
        if (!SendSequencedCommandLocked(record, rfCmd, seq, arg, data))
        {
            return null;
        }
        _lastIssuedSeq[key] = seq;
        _pendingCommands[key] = new Slv3PendingCommand(record.Mac, rfCmd, seq, SequencedCommandBudget - 1, arg, data);
        return seq;
    }

    /// <summary>
    /// Sets the duty a HydroShift II pump is driven at; 0% still runs it at the
    /// head's minimum RPM. Null stops driving it: an AIO with a saved screen keeps
    /// getting params at the default pump speed, any other gets none; nothing
    /// switches the AIO back from RF control. False for a MAC that is not a known
    /// HydroShift II.
    /// </summary>
    public bool SetPumpDuty(string macHex, int? percent)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        var key = Convert.ToHexString(mac);
        lock (_lock)
        {
            if (percent is null)
            {
                // A held screen keeps the AIO under RF control; the next DriveTick drops it otherwise.
                if (_aioControl.TryGetValue(key, out var held) && held.Percent is not null)
                {
                    held.Percent = null;
                    ServiceLog.Info($"[lianli-wireless] HydroShift II {key} pump released");
                }
            }
            else
            {
                if (!TryFindRecordLocked(mac, out var record) || !record.IsHydroShift)
                {
                    return false;
                }
                if (!_aioControl.TryGetValue(key, out var control))
                {
                    control = new Slv3AioControl();
                    _aioControl[key] = control;
                    ServiceLog.Info($"[lianli-wireless] HydroShift II {key} pump driven by Nexus");
                }
                control.Percent = Math.Clamp(percent.Value, 0, 100);
            }
            _anyAioControlled = _aioControl.Count > 0;
            return true;
        }
    }

    private void RaiseAioSwitched(string macHex)
    {
        try
        {
            AioSwitched?.Invoke(macHex);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] AIO switch subscriber failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a held HydroShift II the switch to RF control again, which makes it redraw its
    /// own screen. No-op for an AIO Nexus is not holding.
    /// </summary>
    public void ResendAioSwitch(string macHex)
    {
        lock (_lock)
        {
            if (_aioControl.TryGetValue(macHex.ToUpperInvariant(), out var control))
            {
                control.Switched = false;
                control.SwitchSeq = 0;
            }
        }
    }

    /// <summary>The duty Nexus drives a HydroShift II pump at, or null when it is left alone.</summary>
    public int? GetPumpDuty(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return null;
        }
        lock (_lock)
        {
            return _aioControl.TryGetValue(Convert.ToHexString(mac), out var control) ? control.Percent : null;
        }
    }

    /// <summary>
    /// Uploads one animation frame set to a bound fan chain exactly as
    /// L-Connect's SyncRgbData does: the header packet (index 0) four times
    /// ~20 ms apart, then each data packet once, addressed to the fan's
    /// current (channel, rxType) pipe. Returns false (and sends nothing) if
    /// the fan is unknown, not bound to us, or a send fails partway;
    /// <paramref name="effectIndexHex"/> carries the effect_index actually
    /// sent on success, so the caller can compare it against the fan's next
    /// device-list echo to detect a dropped push.
    /// </summary>
    public bool SendRgbFrame(
        string macHex, ReadOnlySpan<RgbColor> leds, int brightnessPercent, int intervalMs, out string effectIndexHex) =>
        SendRgbData(macHex, Slv3RgbFrame.BuildFrameBuffer(leds, brightnessPercent), leds.Length, 1, intervalMs, dataPasses: 1, out effectIndexHex);

    /// <summary>
    /// <see cref="SendRgbFrame"/> for a looping animation of
    /// <paramref name="frameCount"/> frames (R,G,B per LED, frame-major) that
    /// the chain plays on its own at <paramref name="intervalMs"/> per frame;
    /// each data packet goes out twice.
    /// </summary>
    public bool SendRgbAnimation(
        string macHex, ReadOnlySpan<byte> frames, int ledCount, int frameCount, double intervalMs,
        int brightnessPercent, out string effectIndexHex) =>
        SendRgbData(macHex, Slv3RgbFrame.BuildFrameBuffer(frames, brightnessPercent), ledCount, frameCount, intervalMs, dataPasses: WindowDataPasses, out effectIndexHex);

    /// <summary>
    /// <see cref="SendRgbAnimation"/> for one rolling playback window, re-sent
    /// every fraction of a second while the chain keeps playing: uploaded with
    /// the window profile and a chain-safe effect index.
    /// </summary>
    public async Task<bool> SendRgbWindowAsync(
        string macHex, byte[] frames, int ledCount, int frameCount, double intervalTicks, int brightnessPercent)
    {
        var raw = Slv3RgbFrame.BuildFrameBuffer(frames, brightnessPercent);
        if (!TryPrepareUpload(macHex, raw, ledCount, frameCount, intervalTicks, window: true, out var channel, out var rxType, out var packets, out _))
        {
            return false;
        }
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            for (var i = 0; i < WindowHeaderRepeats; i++)
            {
                if (!SendRfPayloadLocked(channel, rxType, packets[0]))
                {
                    return false;
                }
            }
            for (var pass = 0; pass < WindowDataPasses; pass++)
            {
                for (var p = 1; p < packets.Length; p++)
                {
                    if (!SendRfPayloadLocked(channel, rxType, packets[p]))
                    {
                        return false;
                    }
                }
            }
            NoteConfigChangedLocked();
        }
        // The late passes' cost is booked at decision time so a concurrent
        // upload's check already sees it; the passes themselves are not re-counted.
        var lateCost = WindowLatePasses * (packets.Length - 1);
        var latePasses = 0;
        lock (_lock)
        {
            if (RecentAirSendsLocked() + lateCost <= LatePassAirBudgetPerSecond)
            {
                latePasses = WindowLatePasses;
                var now = _nowMs();
                for (var i = 0; i < lateCost; i++)
                {
                    _airSendsMs.Enqueue(now);
                }
            }
        }
        // Late passes release the lock between packets so the device-list poll keeps running.
        for (var pass = 0; pass < latePasses; pass++)
        {
            await Task.Delay(WindowLateGapMs).ConfigureAwait(false);
            for (var p = 1; p < packets.Length; p++)
            {
                lock (_lock)
                {
                    if (_tx is null || !WriteRfPayloadLocked(channel, rxType, packets[p]))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    // A chain can misparse effect-index bytes as a bind target and re-bind off
    // our pipe, so the first three bytes are its own rx/channel/ordinal (a
    // misparse re-binds it in place). A chain ignores an upload carrying the
    // index it already reports, so the counter skips that value.
    private byte[] NextSafeEffectIndexLocked(Slv3DeviceRecord record)
    {
        byte[] idx;
        do
        {
            _effectCounter = (byte)(_effectCounter + 1);
            if (_effectCounter == 0)
            {
                _effectCounter = 1;
            }
            idx = new byte[] { record.RxType, record.Channel, BindOrdinalLocked(record.Mac), _effectCounter };
        }
        while (record.EffectIndex is { Length: 4 } && idx.AsSpan().SequenceEqual(record.EffectIndex));
        return idx;
    }

    private bool TryPrepareUpload(
        string macHex, byte[] raw, int ledCount, int frameCount, double intervalMs, bool window,
        out byte channel, out byte rxType, out byte[][] packets, out byte[] effectIndex)
    {
        if (IsWiredOnly(macHex))
        {
            channel = 0;
            rxType = 0;
            packets = Array.Empty<byte[]>();
            effectIndex = Array.Empty<byte>();
            return false;
        }
        channel = 0;
        rxType = 0;
        packets = Array.Empty<byte[]>();
        effectIndex = Array.Empty<byte>();
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (_tx is null || !TryFindRecordLocked(mac, out var record) || !IsBoundToUsLocked(record))
            {
                return false;
            }

            byte[] compressed;
            try
            {
                compressed = TinyUz.Compress(raw);
            }
            catch (InvalidOperationException)
            {
                return false;
            }

            effectIndex = window
                ? NextSafeEffectIndexLocked(record)
                : Slv3RgbFrame.BuildEffectIndex(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            packets = Slv3RgbFrame.BuildPackets(
                record.Mac, _masterMac, effectIndex, compressed, ledCount, frameCount, intervalMs);
            channel = record.Channel;
            rxType = record.RxType;
        }

        return true;
    }

    // A lost data packet fails the whole upload and multi-frame loops run to dozens of packets, so animations send each part twice.
    private bool SendRgbData(
        string macHex, byte[] raw, int ledCount, int frameCount, double intervalMs, int dataPasses, out string effectIndexHex)
    {
        effectIndexHex = "";
        if (!TryPrepareUpload(macHex, raw, ledCount, frameCount, intervalMs, window: false, out var channel, out var rxType, out var packets, out var effectIndex))
        {
            return false;
        }

        // The header gaps run with _lock RELEASED so the device-list poll keeps
        // running even with several chains streaming. The repeats are
        // identical, so a keepalive/poll frame slipping into a gap is harmless.
        for (var i = 0; i < RgbHeaderRepeats; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(RgbHeaderGapMs);
            }
            if (!SendRfPayload(channel, rxType, packets[0]))
            {
                return false;
            }
        }

        // Data parts carry the reassembly sequence - send them contiguously under
        // one lock so a concurrent frame can't split them.
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            for (var pass = 0; pass < dataPasses; pass++)
            {
                for (var p = 1; p < packets.Length; p++)
                {
                    if (!SendRfPayloadLocked(channel, rxType, packets[p]))
                    {
                        return false;
                    }
                }
            }
            NoteConfigChangedLocked();
        }

        effectIndexHex = Convert.ToHexString(effectIndex);
        return true;
    }

    // Same as SendRfPayloadLocked but takes _lock itself, for callers that pace
    // sends across released-lock gaps (SendRgbFrame's header repeats).
    private bool SendRfPayload(byte channel, byte rxType, byte[] payload)
    {
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            return SendRfPayloadLocked(channel, rxType, payload);
        }
    }

    // Caller holds _lock.
    private bool SendRfPayloadLocked(byte channel, byte rxType, byte[] payload)
    {
        RecentAirSendsLocked();
        _airSendsMs.Enqueue(_nowMs());
        return WriteRfPayloadLocked(channel, rxType, payload);
    }

    // L-Connect SendRfData attempts every chunk whatever an earlier one returned. A write that
    // times out ends the frame and the rest of the poll's sends, so a stalled TX costs one timeout under the lock.
    private bool WriteRfPayloadLocked(byte channel, byte rxType, byte[] payload)
    {
        if (_pollSendsActive && _txStalledThisPoll)
        {
            return false;
        }
        var sent = true;
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(channel, rxType, payload))
        {
            if (_tx!.RfSend(frame))
            {
                continue;
            }
            sent = false;
            if (_tx.LastWriteTimedOut)
            {
                _txStalledThisPoll = _pollSendsActive;
                break;
            }
        }
        return sent;
    }

    /// <summary>RF payloads handed to the TX over the last <see cref="AirRateWindowMs"/>; drops older entries.</summary>
    private int RecentAirSendsLocked()
    {
        var cutoff = _nowMs() - AirRateWindowMs;
        while (_airSendsMs.Count > 0 && _airSendsMs.Peek() < cutoff)
        {
            _airSendsMs.Dequeue();
        }
        return _airSendsMs.Count;
    }

    /// <summary>
    /// Reads the RX master clock (0.625 ms ticks, GetMac [7..10]) with the
    /// wall-clock ms at the midpoint of the round trip. A chain plays frame
    /// floor(clock / interval) mod frameCount of its uploaded loop.
    /// </summary>
    public bool TryReadRfClock(out uint rfTimer, out double utcMs)
    {
        rfTimer = 0;
        utcMs = 0;
        lock (_lock)
        {
            if (_tx is null)
            {
                return false;
            }
            var before = DateTime.UtcNow.Ticks;
            if (!_tx.RfSend(Slv3Protocol.BuildGetMac(_channel)))
            {
                return false;
            }
            var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
            var after = DateTime.UtcNow.Ticks;
            if (!Slv3Protocol.TryParseGetMac(reply, out _, out rfTimer, out _))
            {
                return false;
            }
            utcMs = ((before + after) / 2.0 - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond;
            return true;
        }
    }

    /// <summary>
    /// Sets a fan chain's port duty target for the next PWM sync: null
    /// follows the
    /// motherboard PWM header, otherwise a manual percent (0..100). Takes
    /// effect on the connection worker's next DriveTick, where the reported
    /// duty's drift from the new target sends the bind/PWM frame. Returns
    /// false for a malformed MAC or a port outside
    /// [0, <see cref="Slv3Protocol.PortsPerRecord"/>).
    /// </summary>
    public bool SetPortDuty(string macHex, int port, int? percent)
    {
        if (IsWiredOnly(macHex))
        {
            return false;
        }
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        if (port < 0 || port >= Slv3Protocol.PortsPerRecord)
        {
            return false;
        }
        lock (_lock)
        {
            var key = Convert.ToHexString(mac);
            if (!_dutyTargets.TryGetValue(key, out var targets))
            {
                targets = new int?[Slv3Protocol.PortsPerRecord];
                _dutyTargets[key] = targets;
            }
            targets[port] = percent is null ? null : Math.Clamp(percent.Value, 0, 100);
        }
        return true;
    }

    /// <summary>Current duty target for a chain's port (null = unset/motherboard-sync).</summary>
    public int? GetPortDuty(string macHex, int port)
    {
        if (!TryParseMac(macHex, out var mac) || port < 0 || port >= Slv3Protocol.PortsPerRecord)
        {
            return null;
        }
        lock (_lock)
        {
            var key = Convert.ToHexString(mac);
            return _dutyTargets.TryGetValue(key, out var targets) ? targets[port] : null;
        }
    }

    /// <summary>Sets our operating channel; must be the default or an odd value (firmware rejects even). Every device bound to us is retargeted at once.</summary>
    public bool SetChannel(int channel)
    {
        if (channel != Slv3Protocol.DefaultChannel && (channel < 1 || channel > 39 || channel % 2 == 0))
        {
            return false;
        }
        lock (_lock)
        {
            _channel = (byte)channel;
            State.Channel = channel;
            // L-Connect SwitchChannel: every chain kept bound reports the old channel and is walked over by the reconcile.
            RunPassLocked(() =>
            {
                ReconcileLocked();
                return true;
            });
        }
        return true;
    }

    /// <summary>
    /// System suspend: SaveCfg repeated at MasterDevice.SaveConfig's cadence so the
    /// bindings reach device flash before power drops. The whole sequence runs on the
    /// returned background task; the call waits only for the first attempt, and at
    /// most <see cref="SuspendFirstSendCapMs"/>, so a stuck write cannot hold the
    /// caller. The lock is never held across a wait, a send that finds it busy is
    /// skipped, and a down link or disposed hub ends the sequence at once.
    /// </summary>
    public Task OnSystemSuspending()
    {
        Volatile.Write(ref _powerQuietUntilMs, long.MaxValue);
        if (_disposed || !IsConnected)
        {
            return Task.CompletedTask;
        }
        var firstAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = Task.Run(() =>
        {
            var sent = 0;
            try
            {
                for (var i = 1; i <= SuspendSaveCfgSends; i++)
                {
                    if (i > 1)
                    {
                        // Gap between tries per MasterDevice.SaveConfig, outside the lock.
                        SleepMs(SuspendSaveCfgGapMs);
                    }
                    var result = TrySuspendSaveCfg(i);
                    firstAttempted.TrySetResult();
                    if (result == SuspendSend.Down)
                    {
                        break;
                    }
                    sent += result == SuspendSend.Sent ? 1 : 0;
                }
                ServiceLog.Info($"[lianli-wireless] suspend SaveCfg sent {sent}/{SuspendSaveCfgSends}");
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[lianli-wireless] suspend SaveCfg failed: {ex.Message}");
            }
            finally
            {
                firstAttempted.TrySetResult();
            }
        });
        firstAttempted.Task.Wait(SuspendFirstSendCapMs);
        return sequence;
    }

    private enum SuspendSend { Sent, Skipped, Failed, Down }

    private SuspendSend TrySuspendSaveCfg(int number)
    {
        if (!Monitor.TryEnter(_lock, SuspendLockWaitMs))
        {
            ServiceLog.Warn($"[lianli-wireless] suspend SaveCfg {number} skipped, hub busy");
            return SuspendSend.Skipped;
        }
        try
        {
            if (_disposed || !IsConnected || _tx is not { IsOpen: true })
            {
                return SuspendSend.Down;
            }
            return SendSaveCfgLocked() ? SuspendSend.Sent : SuspendSend.Failed;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // One lock wait, one TX write timeout (the transport's pipe timeout) and a margin.
    private const int SuspendLockWaitDefaultMs = 300;
    private const int SuspendWriteTimeoutMs = 500;
    private const int SuspendCapMarginMs = 100;
    internal int SuspendLockWaitMs { get; init; } = SuspendLockWaitDefaultMs;
    internal int SuspendFirstSendCapMs { get; init; } = SuspendLockWaitDefaultMs + SuspendWriteTimeoutMs + SuspendCapMarginMs;
    private const int SuspendSaveCfgSends = 3;
    private const int SuspendSaveCfgGapMs = 200;

    private static bool TryParseMac(string macHex, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(macHex))
        {
            return false;
        }
        var clean = macHex.Replace(":", "").Replace("-", "").Trim();
        if (clean.Length != Slv3Protocol.MacLength * 2)
        {
            return false;
        }
        try
        {
            mac = Convert.FromHexString(clean);
            return true;
        }
        catch (FormatException)
        {
            return false;
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
            DisconnectLocked();
        }
    }

    // L-Connect bind_to_master with its target_rx_type; Unbinding aims the chain at a zero master.
    private sealed class BindIntent
    {
        public BindIntent(byte targetRx) => TargetRx = targetRx;

        public byte TargetRx;
        public bool Unbinding;
        // Started by a manual Bind; with the lock off it is sent until the chain confirms.
        public bool Manual;
        public bool NoSlotLogged;
        public int Sends;
    }

    // An unbind waiting for the chain's wireless theme switch to clear; ClearedAtMs is when the bits were seen cleared and the config save went out.
    private sealed class DeferredUnbind(long deadlineMs, string macHex)
    {
        public long DeadlineMs { get; set; } = deadlineMs;
        public bool Extended { get; set; }
        public string MacHex { get; } = macHex;
        public long ClearedAtMs { get; set; } = -1;
        public bool SavedAgain { get; set; }
    }

    private readonly record struct Slv3PendingCommand(byte[] Mac, byte RfCmd, byte TargetSeq, int SendsRemaining, byte Arg = 0, byte[]? Data = null);

    // BlindMsAtSeen = the hub's blind total at that sighting, so expiry can leave out later blind time.
    private readonly record struct Slv3KnownChain(Slv3DeviceRecord Record, long LastSeenMs, long Poll, long BlindMsAtSeen);

    private readonly record struct Slv3Unconfirmed(long FirstPoll, long LastPoll, int Sightings);

    private readonly record struct Slv3BindingLog(long LastMs, int Unlogged);

    // SwitchSeq is the cmdSeq of the last RF_AioSwitchWireless sent; 0 = none in flight.
    private sealed class Slv3AioControl
    {
        /// <summary>The pump duty Nexus drives; null while only the screen is held.</summary>
        public int? Percent;
        public byte SwitchSeq;
        public bool Switched;
    }
}

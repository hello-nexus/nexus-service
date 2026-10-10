using System;
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
/// USBPcap (2026-09-04): <see cref="PollTick"/> is its 500 ms RefreshList +
/// SyncControlInfo pass, <see cref="DriveTick"/> adds the once-a-second
/// SyncPwm, QuerryMasterMac, SyncMasterClock and SaveConfig work.
/// All hardware I/O and shared state are guarded by one lock: the connection
/// worker's ticks and route-driven bind/unbind/identify calls both touch it.
/// </summary>
public sealed class Slv3Hub : IDisposable
{
    // Shared read-only default (all null = motherboard-sync) for a chain that
    // has never had a duty set. Never mutated - safe to share across keys.
    private static readonly int?[] DefaultDutyTargets = new int?[Slv3Protocol.PortsPerRecord];

    private readonly ISlv3Discovery _discovery;
    private readonly Func<Slv3PortInfo, ISlv3Transport> _transportFactory;
    private readonly object _lock = new();
    private readonly Dictionary<string, Slv3PendingOp> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slv3PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
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

    // A validated record can still carry a corrupted MAC, so a new chain is
    // listed only once enough polls within the confirm window repeat it.
    private readonly Dictionary<string, Slv3Unconfirmed> _unconfirmedChains = new(StringComparer.Ordinal);
    private long _devicePolls;
    internal const int ChainConfirmWindowPolls = 10;
    internal const int ChainConfirmSightings = 3;
    // Polls a chain must have been seen in before it counts as owned or can take part in a slot conflict.
    internal const int EstablishedChainPolls = 20;

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

    // An owned device found unbound or off our channel is bound back with a
    // doubling wait between attempts, capped; it never gives up while owned.
    internal const long AutoRebindIntervalMs = 30_000;
    internal const long AutoRebindMaxIntervalMs = 300_000;
    // Fresh polls two chains must report one slot before one is moved.
    internal const int SlotConflictPolls = 5;
    private readonly Dictionary<int, int> _slotConflicts = new();
    private long _conflictPoll;

    internal Action<int> SleepMs { get; init; } = Thread.Sleep;
    private readonly Dictionary<string, AutoRebindState> _rebind = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutoRebindState> _moves = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RebindSighting> _rebindSeen = new(StringComparer.Ordinal);
    internal const int RebindConfirmPolls = 3;

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
    private const long ChainExpiryMs = 30_000;
    // Unseen this long = telemetry is last-known, surfaced as Stale.
    private const long ChainStaleMs = 3_500;

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
    // RX handle, not UsbResetAnother.
    private const int RxBusyPollsBeforeReset = 60;
    private int _rxBusyStreak;
    private byte[]? _firstBusyReply;

    // L-Connect MasterDevice.ResetTx: consecutive TX reopen failures ->
    // UsbResetAnother written on the RX handle, then the TX keeps reopening.
    private const int TxFailsBeforeReset = 5;
    private int _txOpenFails;
    private int _txRecoveries;
    private long _nextTxRecoveryMs;

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

    // SaveCfg after a confirmed bind: L-Connect broadcasts one on every loop
    // pass for a few seconds after Bind() (lastBindTime); here one per poll
    // tick for the same span.
    private const int SaveCfgBurstSends = 10;
    private int _saveCfgBurstRemaining;

    // Throttled SaveCfg after any binding or effect change (RFController.SaveConfig):
    // fires SaveCfgDelayMs after the first change, deferred by the same delay
    // while the last change is younger than SaveCfgQuietMs. A live RGB stream
    // never settles, so it saves once when the stream goes quiet.
    private const long SaveCfgDelayMs = 10_000;
    private const long SaveCfgQuietMs = 5_000;
    private long _saveCfgDueMs;
    private long _lastConfigChangeMs;

    // Poll ticks a pending bind/unbind may re-send before it is dropped as
    // non-converging (fan unreachable).
    internal const int PendingOpTickBudget = 30;

    // Sends a sequenced control frame (RF_Select / RF_RebootLcd) gets before it
    // is dropped without an echo (L-Connect selected / reboot_lcd counters).
    internal const int SequencedCommandBudget = 10;

    // Header packet repeats for an RGB upload, the only profile L-Connect uses
    // (no CRC on this link; a lost header sticks until the effect_index echo
    // shows it).
    private const int RgbHeaderRepeats = 4;
    private const int RgbHeaderGapMs = 20;
    internal int HeaderGapMs { get; set; } = RgbHeaderGapMs;

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
    private readonly Dictionary<string, byte[]> _lastSentEffectIndex = new(StringComparer.Ordinal);

    // Device-list records span more than one page once enough chains are bound
    // (up to MaxSlot, plus non-fan devices), so the poll requests
    // ceil(count/RecordsPerPage) pages. Clamp so a corrupt count can't trigger
    // a runaway read.
    private const int MaxDeviceListPages = 3;
    private int _lastRecordCount;

    public Slv3Hub(ISlv3Discovery discovery, Func<Slv3PortInfo, ISlv3Transport> transportFactory, Func<long>? nowMs = null)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

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
            if (txPort is null || rxPort is null)
            {
                // The module presents TX and RX as two WinUSB devices behind its
                // own hub, so one half missing is a real state (a failed
                // enumeration on that port), not "nothing plugged in".
                State.LinkStatus = txPort is null && rxPort is null ? Slv3LinkStatus.None
                    : txPort is null ? Slv3LinkStatus.TxMissing
                    : Slv3LinkStatus.RxMissing;
                return false;
            }

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

            if (!MasterInitLocked())
            {
                State.LinkStatus = _probesSinceLastMac >= Slv3Protocol.ChannelScanOrder().Length
                    ? Slv3LinkStatus.NoResponse
                    : Slv3LinkStatus.Unknown;
                ServiceLog.Warn("[lianli-wireless] GetMac failed on connect");
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
        _rescueStartMs = -1;
        _unconfirmedChains.Clear();
        _pending.Clear();
        _pendingCommands.Clear();
        _lastIssuedSeq.Clear();
        _rebind.Clear();
        _rebindSeen.Clear();
        _moves.Clear();
        _slotConflicts.Clear();
        _rxAbsentSinceMs = 0;
        _txAbsentSinceMs = 0;
        _nextRxOpenMs = 0;
        _rxOpenFailures = 0;
        foreach (var control in _aioControl.Values)
        {
            control.Switched = false;
            control.SwitchSeq = 0;
        }
        _rxFailStreak = 0;
        _rxBusyStreak = 0;
        _rxRecoveries = 0;
        _nextRxRecoveryMs = 0;
        _txOpenFails = 0;
        _txRecoveries = 0;
        _nextTxRecoveryMs = 0;
        _bothAbsentSinceMs = 0;
        _saveCfgBurstRemaining = 0;
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
        if (txPort is null && rxPort is null)
        {
            if (_bothAbsentSinceMs == 0)
            {
                _bothAbsentSinceMs = nowMs;
            }
            if (nowMs - _bothAbsentSinceMs >= LinkAbsentDisconnectMs)
            {
                ServiceLog.Warn($"[lianli-wireless] neither dongle enumerated for {LinkAbsentDisconnectMs / 1000}s, link down");
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
            NoteTxOpenFailureLocked(nowMs);
            return;
        }
        try
        {
            _tx = _transportFactory(txPort);
        }
        catch (Exception)
        {
            NoteTxOpenFailureLocked(nowMs);
            return;
        }
        if (!TryGetMacOnChannelLocked(_channel))
        {
            try { _tx.Dispose(); } catch { /* best effort */ }
            _tx = null;
            NoteTxOpenFailureLocked(nowMs);
            return;
        }
        _txOpenFails = 0;
        _txRecoveries = 0;
        _nextTxRecoveryMs = 0;
        // The restarted TX lost the LCD video arming; the LCD loop re-arms it.
        _videoModeActive = false;
        _videoModePreppedCount = 0;
        ServiceLog.Info("[lianli-wireless] TX reopened");
    }

    // L-Connect ResetTx: UsbResetAnother on the RX handle restarts the TX.
    private void NoteTxOpenFailureLocked(long nowMs)
    {
        if (++_txOpenFails < TxFailsBeforeReset || nowMs < _nextTxRecoveryMs)
        {
            return;
        }
        _txOpenFails = 0;
        _txRecoveries++;
        _nextTxRecoveryMs = nowMs + RecoveryBackoffFor(_txRecoveries);
        if (_rx is { IsOpen: true } && _rx.RfSend(Slv3Protocol.BuildResetAnother()))
        {
            ServiceLog.Warn($"[lianli-wireless] {TxFailsBeforeReset} consecutive TX open failures, resetting TX via RX ({_txRecoveries})");
        }
    }

    private static long RecoveryBackoffFor(int recoveries) =>
        Math.Min(RecoveryBackoffMs << Math.Min(recoveries - 1, 4), RecoveryBackoffMaxMs);

    private bool MasterInitLocked()
    {
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
        if (TryGetMacOnChannelLocked(_channel))
        {
            _probesSinceLastMac = 0;
            return true;
        }
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
            if (TryGetMacOnChannelLocked(channel))
            {
                ServiceLog.Info($"[lianli-wireless] master found on channel {channel} (scanned from {_channel})");
                _channel = channel;
                State.Channel = channel;
                _probesSinceLastMac = 0;
                return true;
            }
        }
        return false;
    }

    private bool TryGetMacOnChannelLocked(byte channel)
    {
        if (_tx is null || !_tx.RfSend(Slv3Protocol.BuildGetMac(channel)))
        {
            return false;
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        if (!Slv3Protocol.TryParseGetMac(reply, out var mac, out _, out var fw)
            || Slv3Protocol.MacIsZero(mac))
        {
            return false;
        }
        _masterMac = mac;
        State.MasterMac = Convert.ToHexString(mac);
        State.TxFirmwareVersion = fw;
        State.Channel = channel;
        return true;
    }

    /// <summary>
    /// The 500 ms pass (L-Connect RefreshList + SyncControlInfo): refresh the
    /// device list, resolve pending bind/unbind/select/reboot against the fresh
    /// report, and re-send whatever is still pending. Returns false only when
    /// the link is down; a faulty dongle half is recovered in place.
    /// </summary>
    public bool PollTick()
    {
        _usbAioMac = ReadUsbAioMac();
        EnsureOwnedLoaded();
        try
        {
            lock (_lock)
            {
                return PollLocked();
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
        var aio = _anyAioControlled || _anyAioBound;
        var aioSensors = aio ? ReadAioSensors() : default;
        var aioScreens = aio ? ReadAioScreens() : null;
        EnsureOwnedLoaded();
        try
        {
            return DriveLocked(aioSensors, aioScreens);
        }
        finally
        {
            FlushOwned();
        }
    }

    private bool DriveLocked(Slv3AioSensors aioSensors, IReadOnlyDictionary<string, Slv3AioScreen>? aioScreens)
    {
        lock (_lock)
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
            SendClockHeartbeatLocked();
            RunSaveCfgScheduleLocked();
            RescueStrandedChainLocked();
            return true;
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

        // Pending binds, commands and saves keep their budgets while the TX is
        // away instead of spending them on sends that cannot go out.
        if (_tx is not { IsOpen: true })
        {
            return true;
        }
        ResolvePendingLocked();
        TrackOwnedLocked();
        AutoRebindLocked();
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

    // Any established device seen bound to our master is ours, which also seeds
    // the list on upgrade. A young chain may be a corrupt copy of a real one.
    private void TrackOwnedLocked()
    {
        foreach (var record in _lastFanRecords)
        {
            if (!IsBoundToUsLocked(record))
            {
                continue;
            }
            var key = Convert.ToHexString(record.Mac);
            if (_userUnbound.Contains(key) || (_pending.TryGetValue(key, out var op) && op.Unbind))
            {
                continue;
            }
            var known = _owned.TryGetValue(key, out var slot);
            if (known ? slot == record.RxType || SlotSharedLocked(record) : !EstablishedLocked(key))
            {
                continue;
            }
            _owned[key] = record.RxType;
            _ownedDirty = true;
        }
    }

    private bool EstablishedLocked(string key) =>
        _knownChains.TryGetValue(key, out var chain) && chain.Sightings >= EstablishedChainPolls;

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

    // L-Connect SyncControlInfo for owned devices: bind one back when it reports
    // no master or sits off our channel, addressed to its current pipe. A device
    // on a non-zero foreign master is never touched.
    private void AutoRebindLocked()
    {
        if (Slv3Protocol.MacIsZero(_masterMac) || _owned.Count == 0)
        {
            ResolveSlotConflictsLocked();
            return;
        }
        var now = _nowMs();
        foreach (var key in new List<string>(_rebindSeen.Keys))
        {
            if (!_owned.ContainsKey(key))
            {
                _rebindSeen.Remove(key);
            }
        }
        foreach (var record in _lastFanRecords)
        {
            var key = Convert.ToHexString(record.Mac);
            if (!_owned.TryGetValue(key, out var lastSlot))
            {
                continue;
            }
            var zeroMaster = Slv3Protocol.MacIsZero(record.MasterMac);
            var ours = Slv3Protocol.MacEquals(record.MasterMac, _masterMac);
            var settled = ours && IsBoundToUsLocked(record) && record.Channel == _channel;
            if (!(zeroMaster || ours) || settled)
            {
                _rebind.Remove(key);
                _rebindSeen.Remove(key);
                continue;
            }
            if (_pending.ContainsKey(key))
            {
                continue;
            }
            _rebind.TryGetValue(key, out var state);
            if (state.Attempts > 0 && now - state.LastAttemptMs < RebindWaitMs(state.Attempts))
            {
                continue;
            }
            // A corrupt record can show a zero master once, so every attempt, retries included, needs its own
            // run of fresh abnormal sightings. A poll that misses the chain is an RF gap and counts for nothing.
            if (!_knownChains.TryGetValue(key, out var chain) || chain.Poll != _devicePolls)
            {
                continue;
            }
            _rebindSeen.TryGetValue(key, out var seen);
            if (seen.LastPoll != _devicePolls)
            {
                seen = new RebindSighting(_devicePolls, seen.Count + 1);
                _rebindSeen[key] = seen;
            }
            if (seen.Count < RebindConfirmPolls)
            {
                continue;
            }
            var drift = ours && IsBoundToUsLocked(record);
            var slot = drift ? record.RxType
                : lastSlot >= Slv3Protocol.MinSlot && lastSlot <= Slv3Protocol.MaxSlot && !SlotInUseLocked(lastSlot) ? lastSlot
                : FirstFreeSlotLocked();
            if (slot < 0)
            {
                continue;
            }
            _rebind[key] = new AutoRebindState(now, state.Attempts + 1);
            ServiceLog.Info($"[lianli-wireless] auto-rebind {key} {(drift ? "off our channel" : "unbound")}, binding to slot {slot} (attempt {state.Attempts + 1})");
            StartBindLocked(record, (byte)slot, auto: true);
        }
        ResolveSlotConflictsLocked();
    }

    private static long RebindWaitMs(int attempts) =>
        Math.Min(AutoRebindIntervalMs << Math.Min(attempts - 1, 8), AutoRebindMaxIntervalMs);

    // L-Connect unbinds one of two chains reporting a slot for more than four
    // polls; here the loser moves to the first free slot instead.
    private void ResolveSlotConflictsLocked()
    {
        if (_devicePolls == _conflictPoll)
        {
            return;
        }
        _conflictPoll = _devicePolls;
        var bySlot = new Dictionary<int, List<Slv3DeviceRecord>>();
        foreach (var record in _lastFanRecords)
        {
            var key = Convert.ToHexString(record.Mac);
            if (!IsBoundToUsLocked(record) || !_knownChains.TryGetValue(key, out var chain)
                || chain.Poll != _devicePolls || chain.Sightings < EstablishedChainPolls)
            {
                continue;
            }
            if (!bySlot.TryGetValue(record.RxType, out var group))
            {
                bySlot[record.RxType] = group = new List<Slv3DeviceRecord>();
            }
            group.Add(record);
        }
        foreach (var slot in new List<int>(_slotConflicts.Keys))
        {
            if (!bySlot.TryGetValue(slot, out var g) || g.Count < 2)
            {
                _slotConflicts.Remove(slot);
            }
        }
        var contested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in bySlot.Values)
        {
            if (group.Count >= 2)
            {
                foreach (var record in group)
                {
                    contested.Add(Convert.ToHexString(record.Mac));
                }
            }
        }
        foreach (var key in new List<string>(_moves.Keys))
        {
            if (!contested.Contains(key))
            {
                _moves.Remove(key);
            }
        }
        var now = _nowMs();
        foreach (var (slot, group) in bySlot)
        {
            if (group.Count < 2)
            {
                continue;
            }
            var count = _slotConflicts.GetValueOrDefault(slot) + 1;
            if (count < SlotConflictPolls)
            {
                _slotConflicts[slot] = count;
                continue;
            }
            _slotConflicts.Remove(slot);
            var mover = PickConflictMover(group, slot);
            var moverKey = Convert.ToHexString(mover.Mac);
            var target = FirstFreeSlotLocked();
            _moves.TryGetValue(moverKey, out var move);
            if (target < 0 || _pending.ContainsKey(moverKey)
                || (move.Attempts > 0 && now - move.LastAttemptMs < RebindWaitMs(move.Attempts)))
            {
                continue;
            }
            _moves[moverKey] = new AutoRebindState(now, move.Attempts + 1);
            ServiceLog.Info($"[lianli-wireless] slot {slot} reported by {group.Count} chains, moving {moverKey} to slot {target}");
            StartBindLocked(mover, (byte)target, auto: true);
        }
    }

    // Prefers the chain whose owned slot differs from the contested one; ties go to the higher MAC.
    private Slv3DeviceRecord PickConflictMover(List<Slv3DeviceRecord> group, int slot)
    {
        Slv3DeviceRecord best = default;
        var bestKey = "";
        var bestDiffers = false;
        foreach (var record in group)
        {
            var key = Convert.ToHexString(record.Mac);
            var differs = _owned.TryGetValue(key, out var owned) && owned != slot;
            if (bestKey.Length == 0
                || (differs && !bestDiffers)
                || (differs == bestDiffers && string.CompareOrdinal(key, bestKey) > 0))
            {
                best = record;
                bestKey = key;
                bestDiffers = differs;
            }
        }
        return best;
    }

    // Re-send every pending bind/unbind frame and sequenced command, and one
    // SaveCfg of a post-bind burst. L-Connect does this on every loop pass
    // until the device list echoes the result.
    private void SyncControlLocked()
    {
        List<string>? abandoned = null;
        foreach (var (key, op) in _pending)
        {
            if (TryFindRecordLocked(op.Mac, out var record))
            {
                // An automatic bind stops the moment the device shows a foreign master.
                if (op.Auto && !op.Unbind && !Slv3Protocol.MacIsZero(record.MasterMac)
                    && !Slv3Protocol.MacEquals(record.MasterMac, _masterMac))
                {
                    (abandoned ??= new List<string>()).Add(key);
                    continue;
                }
                SendBindFrameLocked(record, op.TargetSlot, op.Unbind);
            }
        }
        if (abandoned is not null)
        {
            foreach (var key in abandoned)
            {
                _pending.Remove(key);
                ServiceLog.Info($"[lianli-wireless] auto-rebind {key} dropped, device is bound to another master");
            }
        }

        List<string>? done = null;
        List<(string Key, Slv3PendingCommand Cmd)>? ticked = null;
        foreach (var (key, cmd) in _pendingCommands)
        {
            var found = TryFindRecordLocked(cmd.Mac, out var record);
            if ((found && record.CmdSeq == cmd.TargetSeq) || cmd.SendsRemaining <= 0)
            {
                (done ??= new List<string>()).Add(key);
                continue;
            }
            if (found)
            {
                SendSequencedCommandLocked(record, cmd.RfCmd, cmd.TargetSeq, cmd.Arg);
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

        if (_saveCfgBurstRemaining > 0)
        {
            _saveCfgBurstRemaining--;
            SendSaveCfgLocked();
        }
    }

    // L-Connect SyncPwm: for every chain bound to us that reports fans, send
    // the bind/PWM frame only when a port's reported duty byte is more than
    // PwmDriftThreshold from its target. The chain echoes the tuple it was
    // last given, so a changed target converges after one frame and a settled
    // chain gets nothing. A chain with a pending bind/unbind is left to the
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
            if (_pending.ContainsKey(key))
            {
                continue;
            }
            var targets = DutyTargetsLocked(record.Mac);
            if (record.FanCount <= 0 && !HasManualTarget(targets))
            {
                continue;
            }
            var pwm = Slv3Protocol.BuildPwmTuple(targets, record.FanCount, record.Family);
            if (Slv3Protocol.NeedSyncPwm(record.Pwm, pwm))
            {
                SendBindFrameLocked(record, record.RxType, unbind: false);
            }
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
            var block = Slv3Protocol.BuildAioParamBlock(
                sensors, Slv3Protocol.HydroShiftPumpTimer(rpm, record.DevType),
                screenSet ? screen : Slv3AioScreen.Default,
                RadiatorFanRpm(record));
            var payload = Slv3Protocol.BuildAioParams(record.Mac, _masterMac, record.RxType, record.Channel, BindOrdinalLocked(record.Mac), block);
            SendRfPayloadLocked(record.Channel, record.RxType, payload);
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
    // values; the link is not torn down for it.
    private void RefreshMasterMacLocked()
    {
        TryGetMacOnChannelLocked(_channel);
    }

    private void ResolvePendingLocked()
    {
        List<string>? resolved = null;
        List<string>? expired = null;
        List<(string Key, Slv3PendingOp Op)>? ticked = null;
        var bindConfirmed = false;
        foreach (var (key, op) in _pending)
        {
            if (TryFindRecordLocked(op.Mac, out var record))
            {
                var done = op.Unbind
                    ? !IsBoundToUsLocked(record)
                    : IsBoundToUsLocked(record) && record.Channel == _channel && record.RxType == op.TargetSlot;
                if (done)
                {
                    (resolved ??= new List<string>()).Add(key);
                    bindConfirmed |= !op.Unbind;
                    if (op.Auto)
                    {
                        ServiceLog.Info($"[lianli-wireless] auto-rebind {key} bound to slot {record.RxType}");
                    }
                    _rebind.Remove(key);
                    continue;
                }
            }
            if (op.TicksRemaining <= 1)
            {
                (expired ??= new List<string>()).Add(key);
            }
            else
            {
                (ticked ??= new List<(string, Slv3PendingOp)>()).Add((key, op with { TicksRemaining = op.TicksRemaining - 1 }));
            }
        }
        if (ticked is not null)
        {
            foreach (var (key, op) in ticked)
            {
                _pending[key] = op;
            }
        }
        if (resolved is not null)
        {
            foreach (var key in resolved)
            {
                _pending.Remove(key);
            }
            // Persist the confirmed binding change to fan flash so it survives
            // a power cycle; a bind without SaveCfg lives only in firmware RAM.
            if (bindConfirmed)
            {
                _saveCfgBurstRemaining = SaveCfgBurstSends;
            }
            NoteConfigChangedLocked();
        }
        if (expired is not null)
        {
            foreach (var key in expired)
            {
                if (_pending.TryGetValue(key, out var droppedOp) && droppedOp.Auto)
                {
                    ServiceLog.Warn($"[lianli-wireless] auto-rebind {key} did not converge in {PendingOpTickBudget} polls, retrying after backoff");
                }
                else
                {
                    ServiceLog.Warn($"[lianli-wireless] bind/unbind for {key} did not converge in {PendingOpTickBudget} polls, dropping");
                }
                _pending.Remove(key);
            }
        }
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

    private void RunSaveCfgScheduleLocked()
    {
        if (_saveCfgDueMs == 0)
        {
            return;
        }
        var now = _nowMs();
        if (now < _saveCfgDueMs)
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

    // Stops at the first frame that fails to write, so a stalled TX costs one write timeout.
    private bool SendSaveCfgLocked()
    {
        if (_tx is null)
        {
            return false;
        }
        var payload = Slv3Protocol.BuildSaveCfg(_masterMac);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0xFF, payload))
        {
            if (!_tx.RfSend(frame))
            {
                return false;
            }
        }
        return true;
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
        _devicePolls++;
        for (var i = 0; i < count; i++)
        {
            var offset = Slv3Protocol.RecordHeaderLength + i * Slv3Protocol.RecordLength;
            if (Slv3Protocol.TryParseRecord(reply, offset, out var record) && record.IsWirelessFan)
            {
                var key = Convert.ToHexString(record.Mac);
                if (!_knownChains.ContainsKey(key))
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
                    ServiceLog.Info($"[lianli-wireless] chain {key} appeared ({what})");
                }
                var sightings = (_knownChains.TryGetValue(key, out var prior) ? prior.Sightings : 0) + 1;
                _knownChains[key] = new Slv3KnownChain(record, nowMs, _devicePolls, sightings);
            }
        }

        ExpireChainsLocked(nowMs);

        // Page estimate follows the larger of the reply's count and the tracked
        // set, so a partial report can't shrink the next read below the full list.
        _lastRecordCount = Math.Max(count, _knownChains.Count);
        PublishFansLocked(nowMs);
        return true;
    }

    // Chains age on their own timers whether or not the RX answered.
    private void ExpireChainsLocked(long nowMs)
    {
        List<string>? gone = null;
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
            if (nowMs - chain.LastSeenMs > ChainExpiryMs)
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
                _rebind.Remove(key);
                _rebindSeen.Remove(key);
                if (_aioControl.TryGetValue(key, out var control))
                {
                    control.Switched = false;
                    control.SwitchSeq = 0;
                }
            }
        }
    }

    private void AgeChainsLocked(long nowMs)
    {
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
    private bool IsBoundToUsLocked(Slv3DeviceRecord record) =>
        Slv3Protocol.MacEquals(record.MasterMac, _masterMac)
        && record.RxType >= Slv3Protocol.MinSlot
        && record.RxType <= Slv3Protocol.MaxSlot;

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
        UsbConnected = record.IsHydroShift && _usbAioMac is { } usbMac
            && string.Equals(usbMac, Convert.ToHexString(record.Mac), StringComparison.OrdinalIgnoreCase),
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
                || (_pending.TryGetValue(key, out var op) && !op.Unbind);
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

    // Sent addressed at the fan's CURRENT (channel, rxType) pipe from the last
    // device-list report, so the dongle steers to it regardless of which master
    // it is presently bound to; the payload's target fields carry what to
    // reconfigure to. Caller holds _lock.
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
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(record.Channel, record.RxType, payload))
        {
            if (!_tx.RfSend(frame))
            {
                return false;
            }
        }
        return true;
    }

    // One-off for one customer build: their CPU Strimer left the dongle at
    // 2026-10-08 ~02:00:15Z, when a live upload's timestamp effect index began
    // 0x19 0x3D; a chain misreading that upload as RF_Bind takes [14]/[15] as
    // its rx/channel. Binds it back from the misread pipes near that moment.
    private static readonly byte[] StrandedMac = Convert.FromHexString("B041BC7A4EE0");
    private const byte StrandedRx = 0x19;
    private const byte StrandedChannel = 0x3D;
    private const int RescueChannelSpread = 3;
    private const int RescuePipesPerTick = 12;
    private const long RescueSettleMs = 15_000;
    // Counted across reconnects so a flapping link cannot keep it sending.
    private const int RescueTickBudget = 300;
    private List<(byte Channel, byte Rx)>? _rescuePipes;
    private int _rescueCursor;
    private long _rescueStartMs = -1;
    private bool _rescueSent;
    private int _rescueTicks;
    private bool _rescueStopped;
    private bool _rescueDone;

    private void RescueStrandedChainLocked()
    {
        if (_rescueDone || _tx is null)
        {
            return;
        }
        var now = _nowMs();
        if (_knownChains.TryGetValue(Convert.ToHexString(StrandedMac), out var back))
        {
            // Only a binding to us is persisted; one that came back anywhere else is left alone.
            if (_rescueSent && IsBoundToUsLocked(back.Record))
            {
                ServiceLog.Info($"[lianli-wireless] rescue: B041BC7A4EE0 is back on the dongle at rx {back.Record.RxType}");
                _saveCfgBurstRemaining = SaveCfgBurstSends;
                NoteConfigChangedLocked();
            }
            _rescueDone = true;
            return;
        }
        if (_rescueStopped)
        {
            return;
        }
        // The settle restarts with every connection (DisconnectLocked clears the
        // start) and waits for chains to report, so occupied slots are known.
        if (_knownChains.Count == 0)
        {
            _rescueStartMs = -1;
            return;
        }
        if (_rescueStartMs < 0)
        {
            _rescueStartMs = now;
        }
        if (now - _rescueStartMs < RescueSettleMs)
        {
            return;
        }
        if (_rescueTicks >= RescueTickBudget)
        {
            ServiceLog.Info("[lianli-wireless] rescue: B041BC7A4EE0 did not answer, giving up");
            _rescueStopped = true;
            return;
        }
        var slot = HighestFreeSlotLocked();
        if (slot < 0)
        {
            return;
        }
        _rescuePipes ??= BuildRescuePipes(_channel);
        var payload = Slv3Protocol.BuildBind(StrandedMac, _masterMac, targetRx: (byte)slot, targetChannel: _channel,
            slot: BindOrdinalLocked(StrandedMac), Slv3Protocol.BuildPwmTuple(DefaultDutyTargets, 0));
        _rescueTicks++;
        if (!_rescueSent)
        {
            ServiceLog.Info($"[lianli-wireless] rescue: binding B041BC7A4EE0 to rx {slot} ch {_channel} over {_rescuePipes.Count} pipes");
            _rescueSent = true;
        }
        // The two likeliest pipes go out every tick, the rest rotate; a failed
        // write ends the tick so a stuck TX holds the lock for one timeout only.
        if (!SendRfPayloadLocked(_rescuePipes[0].Channel, _rescuePipes[0].Rx, payload)
            || !SendRfPayloadLocked(_rescuePipes[1].Channel, _rescuePipes[1].Rx, payload))
        {
            return;
        }
        for (var i = 0; i < RescuePipesPerTick - 2; i++)
        {
            var pipe = _rescuePipes[2 + _rescueCursor];
            _rescueCursor = (_rescueCursor + 1) % (_rescuePipes.Count - 2);
            if (!SendRfPayloadLocked(pipe.Channel, pipe.Rx, payload))
            {
                return;
            }
        }
    }

    // Searched from the top: the customer's chains hold the low slots, and a
    // chain still within its expiry keeps its slot even if a poll missed it.
    private int HighestFreeSlotLocked()
    {
        var used = new HashSet<int>();
        foreach (var chain in _knownChains.Values)
        {
            if (IsBoundToUsLocked(chain.Record))
            {
                used.Add(chain.Record.RxType);
            }
        }
        foreach (var record in _lastFanRecords)
        {
            if (IsBoundToUsLocked(record))
            {
                used.Add(record.RxType);
            }
        }
        foreach (var op in _pending.Values)
        {
            if (!op.Unbind)
            {
                used.Add(op.TargetSlot);
            }
        }
        for (var slot = Slv3Protocol.MaxSlot; slot >= Slv3Protocol.MinSlot; slot--)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }
        return -1;
    }

    // Misread channels nearest the drop first at the misread rx, then the misread
    // rx on our channel, then every slot rx on those channels (a chain that moved
    // only its channel).
    private static List<(byte Channel, byte Rx)> BuildRescuePipes(byte ourChannel)
    {
        var channels = new List<byte> { StrandedChannel };
        for (var d = 1; d <= RescueChannelSpread; d++)
        {
            channels.Add((byte)(StrandedChannel - d));
            channels.Add((byte)(StrandedChannel + d));
        }
        var pipes = new List<(byte Channel, byte Rx)>();
        foreach (var ch in channels)
        {
            pipes.Add((ch, StrandedRx));
        }
        pipes.Add((ourChannel, StrandedRx));
        foreach (var ch in channels)
        {
            for (var rx = Slv3Protocol.MinSlot; rx <= Slv3Protocol.MaxSlot; rx++)
            {
                pipes.Add((ch, (byte)rx));
            }
        }
        return pipes;
    }

    private bool SendSequencedCommandLocked(Slv3DeviceRecord record, byte rfCmd, byte cmdSeq, byte arg = 0)
    {
        if (_tx is null)
        {
            return false;
        }
        // lian-li-linux switch_to_wireless_theme carries the AIO's bind ordinal at [16].
        var slot = rfCmd == Slv3Protocol.RfAioSwitchWireless ? BindOrdinalLocked(record.Mac) : (byte)0;
        var payload = Slv3Protocol.BuildSequencedCommand(rfCmd, record.Mac, _masterMac, record.RxType, record.Channel, cmdSeq, slot, arg);
        return SendRfPayloadLocked(record.Channel, record.RxType, payload);
    }

    // Caller holds _lock.
    private int?[] DutyTargetsLocked(byte[] mac)
    {
        var key = Convert.ToHexString(mac);
        return _dutyTargets.TryGetValue(key, out var targets) ? targets : DefaultDutyTargets;
    }

    // L-Connect SyncMasterClock: once a second, broadcast pipe (USB rx 0xFF).
    private void SendClockHeartbeatLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var payload = Slv3Protocol.BuildClockSync(_masterMac, DateTime.Now);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0xFF, payload))
        {
            _tx.RfSend(frame);
        }
    }

    /// <summary>
    /// Requests a bind to the first free slot (1..13); the poll ticks drive the
    /// state machine to completion. Fails if the MAC has never been seen in a
    /// device-list report. A fan already bound to us is left on its current
    /// slot rather than being reassigned a new one.
    /// </summary>
    public bool Bind(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (!TryFindRecordLocked(mac, out var existing))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            if (IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
                return true;
            }
            var slot = FirstFreeSlotLocked();
            if (slot < 0)
            {
                return false;
            }
            _userUnbound.Remove(key);
            _owned[key] = slot;
            _ownedDirty = true;
            StartBindLocked(existing, (byte)slot, auto: false);
        }
        FlushOwned();
        return true;
    }

    private void StartBindLocked(Slv3DeviceRecord record, byte slot, bool auto)
    {
        _rebindSeen.Remove(Convert.ToHexString(record.Mac));
        _pending[Convert.ToHexString(record.Mac)] = new Slv3PendingOp(record.Mac, slot, Unbind: false, PendingOpTickBudget, auto);
        // First frame goes out now; the poll re-sends until the device list
        // confirms, so a request does not wait up to a full tick to start.
        SendBindFrameLocked(record, slot, unbind: false);
    }

    /// <summary>
    /// Requests a release (slot 0, master cleared); the poll ticks drive the
    /// state machine to completion. Fails if the MAC has never been seen in a
    /// device-list report. A fan already unbound is a no-op.
    /// </summary>
    public bool Unbind(string macHex)
    {
        if (!TryParseMac(macHex, out var mac))
        {
            return false;
        }
        lock (_lock)
        {
            if (!TryFindRecordLocked(mac, out var existing))
            {
                return false;
            }
            var key = Convert.ToHexString(mac);
            _userUnbound.Add(key);
            _rebind.Remove(key);
            _rebindSeen.Remove(key);
            if (_owned.Remove(key))
            {
                _ownedDirty = true;
            }
            if (!IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
            }
            else
            {
                _pending[key] = new Slv3PendingOp(mac, 0, Unbind: true, PendingOpTickBudget);
                SendBindFrameLocked(existing, 0, unbind: true);
            }
        }
        FlushOwned();
        return true;
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
    private byte? QueueSequencedCommandLocked(Slv3DeviceRecord record, byte rfCmd, byte arg = 0)
    {
        var key = Convert.ToHexString(record.Mac);
        _lastIssuedSeq.TryGetValue(key, out var lastIssued);
        var seq = Slv3Protocol.NextCmdSeq((byte)Math.Max(lastIssued, record.CmdSeq));
        if (!SendSequencedCommandLocked(record, rfCmd, seq, arg))
        {
            return null;
        }
        _lastIssuedSeq[key] = seq;
        _pendingCommands[key] = new Slv3PendingCommand(record.Mac, rfCmd, seq, SequencedCommandBudget - 1, arg);
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
    /// the window profile.
    /// </summary>
    public async Task<bool> SendRgbWindowAsync(
        string macHex, byte[] frames, int ledCount, int frameCount, double intervalTicks, int brightnessPercent)
    {
        var raw = Slv3RgbFrame.BuildFrameBuffer(frames, brightnessPercent);
        if (!TryPrepareUpload(macHex, raw, ledCount, frameCount, intervalTicks, out var channel, out var rxType, out var packets, out var effectIndex))
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
            NoteEffectIndexSentLocked(macHex, effectIndex);
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
    // index it already shows, so the counter skips the reported and the last
    // sent value (the report lags an upload that landed after the poll).
    private byte[] NextSafeEffectIndexLocked(Slv3DeviceRecord record)
    {
        _lastSentEffectIndex.TryGetValue(Convert.ToHexString(record.Mac), out var lastSent);
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
        while ((record.EffectIndex is { Length: 4 } && idx.AsSpan().SequenceEqual(record.EffectIndex))
            || (lastSent is not null && idx.AsSpan().SequenceEqual(lastSent)));
        return idx;
    }

    private void NoteEffectIndexSentLocked(string macHex, byte[] effectIndex)
    {
        if (TryParseMac(macHex, out var mac))
        {
            _lastSentEffectIndex[Convert.ToHexString(mac)] = effectIndex;
        }
    }

    private bool TryPrepareUpload(
        string macHex, byte[] raw, int ledCount, int frameCount, double intervalMs,
        out byte channel, out byte rxType, out byte[][] packets, out byte[] effectIndex)
    {
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

            effectIndex = NextSafeEffectIndexLocked(record);
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
        if (!TryPrepareUpload(macHex, raw, ledCount, frameCount, intervalMs, out var channel, out var rxType, out var packets, out var effectIndex))
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
                Thread.Sleep(HeaderGapMs);
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
            NoteEffectIndexSentLocked(macHex, effectIndex);
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

    private bool WriteRfPayloadLocked(byte channel, byte rxType, byte[] payload)
    {
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(channel, rxType, payload))
        {
            if (!_tx!.RfSend(frame))
            {
                return false;
            }
        }
        return true;
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
            // Every device bound to us now reports the old channel; retarget them at once.
            _rebind.Clear();
            foreach (var record in _lastFanRecords)
            {
                if (IsBoundToUsLocked(record) && !_pending.ContainsKey(Convert.ToHexString(record.Mac)))
                {
                    StartBindLocked(record, record.RxType, auto: true);
                }
            }
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

    // Slots already used by a fan bound to us, or already claimed by an in-flight
    // bind, are excluded. Caller holds _lock.
    private bool SlotInUseLocked(int slot)
    {
        foreach (var record in _lastFanRecords)
        {
            if (IsBoundToUsLocked(record) && record.RxType == slot)
            {
                return true;
            }
        }
        foreach (var op in _pending.Values)
        {
            if (!op.Unbind && op.TargetSlot == slot)
            {
                return true;
            }
        }
        return false;
    }

    private int FirstFreeSlotLocked()
    {
        var used = new HashSet<int>();
        foreach (var record in _lastFanRecords)
        {
            if (IsBoundToUsLocked(record))
            {
                used.Add(record.RxType);
            }
        }
        foreach (var op in _pending.Values)
        {
            if (!op.Unbind)
            {
                used.Add(op.TargetSlot);
            }
        }
        for (var slot = Slv3Protocol.MinSlot; slot <= Slv3Protocol.MaxSlot; slot++)
        {
            if (!used.Contains(slot))
            {
                return slot;
            }
        }
        return -1;
    }

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

    private readonly record struct Slv3PendingOp(byte[] Mac, byte TargetSlot, bool Unbind, int TicksRemaining, bool Auto = false);

    private readonly record struct AutoRebindState(long LastAttemptMs, int Attempts);

    private readonly record struct RebindSighting(long LastPoll, int Count);

    private readonly record struct Slv3PendingCommand(byte[] Mac, byte RfCmd, byte TargetSeq, int SendsRemaining, byte Arg = 0);

    private readonly record struct Slv3KnownChain(Slv3DeviceRecord Record, long LastSeenMs, long Poll, int Sightings);

    private readonly record struct Slv3Unconfirmed(long FirstPoll, long LastPoll, int Sightings);

    // SwitchSeq is the cmdSeq of the last RF_AioSwitchWireless sent; 0 = none in flight.
    private sealed class Slv3AioControl
    {
        /// <summary>The pump duty Nexus drives; null while only the screen is held.</summary>
        public int? Percent;
        public byte SwitchSeq;
        public bool Switched;
    }
}

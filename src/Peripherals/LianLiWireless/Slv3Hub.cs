using System;
using System.Collections.Generic;
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
    // listed only once another poll within the confirm window repeats it.
    // Value: the poll the MAC was first seen in.
    private readonly Dictionary<string, long> _unconfirmedChains = new(StringComparer.Ordinal);
    private long _devicePolls;
    internal const int ChainConfirmWindowPolls = 10;

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

    // GetDev failure escalation (lian-li-linux controller.rs): 5 consecutive
    // USB-level failures -> UsbResetAnother to the RX MCU (a handle reopen does
    // not reset a wedged radio). A good reply refills the reset budget, so only
    // resets that fail back to back hand the worker its disconnect/reconnect.
    private const int RxFailStreakForReset = 5;
    private const int MaxRxResetsPerConnection = 3;
    // Post-reset settle per the reference's 500 ms sleep after USB_ResetAnother.
    private const int RxResetSettleMs = 500;
    private int _rxFailStreak;
    private int _rxResetCount;

    // A reply opening with 0 is the RX's "no device list this cycle". It does
    // this on its own every ~17 s for a few seconds and recovers without help,
    // so only a busy spell this long is treated as a wedge. A wedge gets a fresh
    // RX handle, not UsbResetAnother: the reset re-enumerates the TX, and the RX
    // reopen is the step a full reconnect adds over it.
    private const int RxBusyPollsBeforeReset = 60;
    private const int MaxRxReopens = 3;
    private int _rxBusyStreak;
    private int _rxReopenCount;
    private byte[]? _firstBusyReply;

    // Polls a TX that went away (it re-enumerates about a second after an RX
    // reset) may take to come back before the link is torn down and rebuilt.
    private const int TxReopenPollBudget = 10;
    private int _txMissingPolls;

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
    /// The link is up: connected, and the RX that reports the device list is
    /// open. The TX can be briefly gone while it re-enumerates after an RX
    /// reset; <see cref="PollTick"/> reopens it without dropping the list.
    /// </summary>
    public bool IsConnected => State.IsConnected && _rx is { IsOpen: true };

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
        if (State.LinkStatus == Slv3LinkStatus.Ok)
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
        foreach (var control in _aioControl.Values)
        {
            control.Switched = false;
            control.SwitchSeq = 0;
        }
        _rxFailStreak = 0;
        _rxBusyStreak = 0;
        _rxResetCount = 0;
        _rxReopenCount = 0;
        _txMissingPolls = 0;
        _saveCfgBurstRemaining = 0;
        _saveCfgDueMs = 0;
        _videoModeActive = false;
        _videoModePreppedCount = 0;
    }

    // Reopens a TX whose handle died with its device, keeping the device list
    // so nothing downstream sees the link drop. The new handle must answer
    // GetMac: a dying instance can still be listed for a moment.
    private bool TryReopenTxLocked()
    {
        try { _tx?.Dispose(); } catch { /* best effort */ }
        _tx = null;
        Slv3PortInfo? txPort = null;
        foreach (var port in _discovery.Discover())
        {
            if (port.Role == Slv3DongleRole.Tx)
            {
                txPort = port;
                break;
            }
        }
        if (txPort is null)
        {
            return false;
        }
        try
        {
            _tx = _transportFactory(txPort);
        }
        catch (Exception)
        {
            return false;
        }
        if (!TryGetMacOnChannelLocked(_channel))
        {
            try { _tx.Dispose(); } catch { /* best effort */ }
            _tx = null;
            return false;
        }
        // The restarted TX lost the LCD video arming; the LCD loop re-arms it.
        _videoModeActive = false;
        _videoModePreppedCount = 0;
        ServiceLog.Info("[lianli-wireless] TX reopened");
        return true;
    }

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
    /// report, and re-send whatever is still pending. Returns false when the
    /// link is down or the poll failed past its reset budget.
    /// </summary>
    public bool PollTick()
    {
        _usbAioMac = ReadUsbAioMac();
        lock (_lock)
        {
            return PollLocked();
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
        if (_tx is { IsOpen: true } || TryReopenTxLocked())
        {
            _txMissingPolls = 0;
        }
        else if (++_txMissingPolls >= TxReopenPollBudget)
        {
            return false;
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
        SyncControlLocked();
        return true;
    }

    // Re-send every pending bind/unbind frame and sequenced command, and one
    // SaveCfg of a post-bind burst. L-Connect does this on every loop pass
    // until the device list echoes the result.
    private void SyncControlLocked()
    {
        foreach (var op in _pending.Values)
        {
            if (TryFindRecordLocked(op.Mac, out var record))
            {
                SendBindFrameLocked(record, op.TargetSlot, op.Unbind);
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
                    : IsBoundToUsLocked(record);
                if (done)
                {
                    (resolved ??= new List<string>()).Add(key);
                    bindConfirmed |= !op.Unbind;
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
                ServiceLog.Warn($"[lianli-wireless] bind/unbind for {key} did not converge in {PendingOpTickBudget} polls, dropping");
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

    private void SendSaveCfgLocked()
    {
        if (_tx is null)
        {
            return;
        }
        var payload = Slv3Protocol.BuildSaveCfg(_masterMac);
        foreach (var frame in Slv3Protocol.BuildUsbSendRf(_channel, 0xFF, payload))
        {
            _tx.RfSend(frame);
        }
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
            // A failed USB write means the handle itself is dead (replug,
            // suspend); fail the tick so the worker reopens promptly.
            return false;
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
        _rxResetCount = 0;
        _rxReopenCount = 0;

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
                    if (ConfirmNewChains
                        && (!_unconfirmedChains.TryGetValue(key, out var firstPoll) || firstPoll == _devicePolls))
                    {
                        _unconfirmedChains.TryAdd(key, _devicePolls);
                        continue;
                    }
                    _unconfirmedChains.Remove(key);
                    var what = record.IsStrimer ? $"Strimer dev_type {record.DevType}"
                        : record.IsHydroShift ? $"HydroShift II dev_type {record.DevType}, {record.FanCount} fan(s)"
                        : $"{record.FanCount} fan(s), {record.Family}";
                    ServiceLog.Info($"[lianli-wireless] chain {key} appeared ({what})");
                }
                _knownChains[key] = new Slv3KnownChain(record, nowMs);
            }
        }

        List<string>? gone = null;
        foreach (var (key, firstPoll) in _unconfirmedChains)
        {
            if (_devicePolls - firstPoll >= ChainConfirmWindowPolls)
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
                if (_aioControl.TryGetValue(key, out var control))
                {
                    control.Switched = false;
                    control.SwitchSeq = 0;
                }
            }
        }

        // Page estimate follows the larger of the reply's count and the tracked
        // set, so a partial report can't shrink the next read below the full list.
        _lastRecordCount = Math.Max(count, _knownChains.Count);
        PublishFansLocked(nowMs);
        return true;
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
        PublishFansLocked(_nowMs());
        if (++_rxBusyStreak == 1)
        {
            _firstBusyReply = reply;
        }
        if (_rxBusyStreak < RxBusyPollsBeforeReset)
        {
            return true;
        }
        if (_rxReopenCount >= MaxRxReopens)
        {
            // Out of reopens: the streak stays past its threshold, so every
            // further busy poll fails and the worker reconnects.
            return false;
        }
        _rxBusyStreak = 0;
        _rxReopenCount++;
        var first = _firstBusyReply is { } b ? Convert.ToHexString(b, 0, Math.Min(b.Length, Slv3Protocol.UsbPacketSize)) : "";
        ServiceLog.Warn($"[lianli-wireless] {RxBusyPollsBeforeReset} consecutive busy GetDev replies (first {first}), reopening RX ({_rxReopenCount}/{MaxRxReopens})");
        return TryReopenRxLocked();
    }

    // Reopens the RX handle, keeping the device list.
    private bool TryReopenRxLocked()
    {
        try { _rx?.Dispose(); } catch { /* best effort */ }
        _rx = null;
        Slv3PortInfo? rxPort = null;
        foreach (var port in _discovery.Discover())
        {
            if (port.Role == Slv3DongleRole.Rx)
            {
                rxPort = port;
                break;
            }
        }
        if (rxPort is null)
        {
            ServiceLog.Warn("[lianli-wireless] RX reopen failed: RX not enumerated");
            return false;
        }
        try
        {
            _rx = _transportFactory(rxPort);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] RX reopen failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        ServiceLog.Info("[lianli-wireless] RX reopened");
        return true;
    }

    // Only a good reply clears the busy and failure streaks, so an RX that
    // alternates between the two still escalates.
    private bool HandleGetDevFailureLocked()
    {
        PublishFansLocked(_nowMs());
        _rxFailStreak++;
        if (_rxFailStreak < RxFailStreakForReset)
        {
            // Transient: keep the last-known list and let the next tick retry.
            return true;
        }
        return ResetRxLocked($"{RxFailStreakForReset} consecutive GetDev failures");
    }

    private bool ResetRxLocked(string reason)
    {
        if (_rxResetCount >= MaxRxResetsPerConnection)
        {
            // Out of resets. The streak stays past its threshold, so every
            // further failed or busy poll lands here and the worker's
            // consecutive-failure path gets its disconnect/reopen.
            return false;
        }
        _rxFailStreak = 0;
        _rxBusyStreak = 0;
        _rxResetCount++;
        ServiceLog.Warn($"[lianli-wireless] {reason}, resetting RX MCU ({_rxResetCount}/{MaxRxResetsPerConnection})");
        if (_rx is not null && _rx.RfSend(Slv3Protocol.BuildResetAnother()))
        {
            _rx.RfRead(Slv3Protocol.UsbPacketSize);
        }
        // Reference sleeps 500 ms after USB_ResetAnother before the next poll.
        Thread.Sleep(RxResetSettleMs);
        return true;
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
            _pending[key] = new Slv3PendingOp(mac, (byte)slot, Unbind: false, PendingOpTickBudget);
            // First frame goes out now; the poll re-sends until the device list
            // confirms, so a request does not wait up to a full tick to start.
            SendBindFrameLocked(existing, (byte)slot, unbind: false);
        }
        return true;
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
            if (!IsBoundToUsLocked(existing))
            {
                _pending.Remove(key);
                return true;
            }
            _pending[key] = new Slv3PendingOp(mac, 0, Unbind: true, PendingOpTickBudget);
            SendBindFrameLocked(existing, 0, unbind: true);
        }
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

    /// <summary>Sets our operating channel; must be the default or an odd value (firmware rejects even). Reaches a bound chain with its next bind/PWM frame.</summary>
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
        }
        return true;
    }

    // Slots already used by a fan bound to us, or already claimed by an in-flight
    // bind, are excluded. Caller holds _lock.
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

    private readonly record struct Slv3PendingOp(byte[] Mac, byte TargetSlot, bool Unbind, int TicksRemaining);

    private readonly record struct Slv3PendingCommand(byte[] Mac, byte RfCmd, byte TargetSeq, int SendsRemaining, byte Arg = 0);

    private readonly record struct Slv3KnownChain(Slv3DeviceRecord Record, long LastSeenMs);

    // SwitchSeq is the cmdSeq of the last RF_AioSwitchWireless sent; 0 = none in flight.
    private sealed class Slv3AioControl
    {
        /// <summary>The pump duty Nexus drives; null while only the screen is held.</summary>
        public int? Percent;
        public byte SwitchSeq;
        public bool Switched;
    }
}
